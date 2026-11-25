using System.Collections.ObjectModel;
using System.Globalization;
using Apparatus.Core;
using Apparatus.Core.Search;
using Apparatus.Core.Search.Providers;
using Apparatus.Core.Start;
using Apparatus.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace Apparatus.App;

/// <summary>
/// Apparatus' own Start menu, in the classic two-column layout: on the left the pinned and the most used apps, "All programs" and the
/// search box; on the right the places of the system (folders, This PC, Control Panel, Settings...) and the power menu.
/// </summary>
public sealed partial class StartMenuWindow : Window
{
    private const double WidthDip = 700;
    private const double HeightDip = 740;
    private const double GapDip = 12;
    private const int RecentCount = 6;

    private readonly AppServices _services;
    private readonly ResultExecutor _executor;
    private readonly IconCache _icons;
    private readonly nint _hwnd;
    private readonly RegistryTweakStore _registry = new();
    private readonly ObservableCollection<ResultItemViewModel> _pinned = new();
    private readonly ObservableCollection<ResultItemViewModel> _results = new();
    private readonly ObservableCollection<ResultItemViewModel> _allApps = new();
    private CancellationTokenSource? _searchCts;
    private DateTime _shownAtUtc;
    private bool _allowClose;
    private bool _prewarming;
    private bool _selfChange;
    private bool _dialogOpen;
    private bool _showingAllApps;
    private bool _rebuildQueued;

    public StartMenuWindow(AppServices services, ResultExecutor executor)
    {
        _services = services;
        _executor = executor;
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _icons = new IconCache(DispatcherQueue);
        PinnedList.ItemsSource = _pinned;
        ResultsList.ItemsSource = _results;
        AllAppsList.ItemsSource = _allApps;

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon(AppPaths.AssetPath("apparatus.ico"));
        WindowHelpers.UseRoundedCorners(_hwnd);

        BuildPowerMenu();
        BuildPlaces();
        ApplyAppearance();

        Activated += OnActivated;
        AppWindow.Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            HideMenu();
        };
        _services.StartLayout.Changed += (_, _) => DispatcherQueue.TryEnqueue(() => { if (!_selfChange) QueueRebuild(); });
        _services.Settings.Changed += (_, _) => DispatcherQueue.TryEnqueue(() => { ApplyAppearance(); QueueRebuild(); });
    }

    public bool IsMenuVisible { get; private set; }

    private int _actionCount;

    /// <summary>How many times the menu has been shown or hidden. A watchdog uses it to tell that a request was carried out.</summary>
    public int ActionCount => Volatile.Read(ref _actionCount);

    public void AllowClose() => _allowClose = true;

    // ---------------------------------------------------------- show/hide

    /// <summary>Renders the window once off-screen so the first real show is instant.</summary>
    public void Prewarm(Action? done = null)
    {
        _prewarming = true;
        RebuildPinned();
        AppWindow.MoveAndResize(new RectInt32(-12000, -12000, (int)WidthDip, (int)HeightDip));
        AppWindow.Show(activateWindow: false);

        void OnRendered(object? s, object e)
        {
            CompositionTarget.Rendered -= OnRendered;
            AppWindow.Hide();
            _prewarming = false;
            done?.Invoke();
        }
        CompositionTarget.Rendered += OnRendered;
    }

    public void ToggleMenu()
    {
        if (IsMenuVisible) HideMenu();
        else ShowMenu();
    }

    public void ShowMenu()
    {
        if (_prewarming) AppWindow.Hide();
        _services.Expander?.RememberLauncherTarget(KeyTranslator.ForegroundWindow());
        _ = _services.RefreshAppsAsync();
        RefreshAvatar();
        EnsureSeeded();

        ResetView();
        RebuildPinned();
        PositionNearTaskbar();

        IsMenuVisible = true;
        _shownAtUtc = DateTime.UtcNow;
        Interlocked.Increment(ref _actionCount);
        AppLog.Info("Start menu shown");
        AppWindow.Show();
        Activate();
        WindowHelpers.BringToForeground(_hwnd);
        DispatcherQueue.TryEnqueue(() => SearchBox.Focus(FocusState.Programmatic));
    }

    /// <summary>Opens the menu already searching for <paramref name="query"/> (used by the command line and by tests).</summary>
    public void ShowWithQuery(string query)
    {
        ShowMenu();
        SearchBox.Text = query;
    }

    /// <summary>Opens the menu on the alphabetical list of every app.</summary>
    public void ShowAllApps()
    {
        ShowMenu();
        OnAllAppsClick(this, new RoutedEventArgs());
    }

    public void HideMenu()
    {
        if (!IsMenuVisible) return;
        IsMenuVisible = false;
        Interlocked.Increment(ref _actionCount);
        AppLog.Info("Start menu hidden");
        _searchCts?.Cancel();
        AppWindow.Hide();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // Like Windows' own menu: clicking elsewhere dismisses it. A short grace period covers the focus churn on opening.
        if (args.WindowActivationState == WindowActivationState.Deactivated && IsMenuVisible && !_dialogOpen &&
            (DateTime.UtcNow - _shownAtUtc).TotalMilliseconds > 300)
        {
            HideMenu();
        }
    }

    private void PositionNearTaskbar()
    {
        var (cx, cy) = WindowHelpers.CursorPosition();
        var area = DisplayArea.GetFromPoint(new PointInt32(cx, cy), DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Move(new PointInt32(area.X + 40, area.Y + 40));   // first, so the window takes that monitor's DPI
        double scale = WindowHelpers.GetScale(_hwnd);

        var taskbar = TaskbarInfo.Get();
        var edge = taskbar is { } t ? (ScreenEdge)(int)t.Edge : ScreenEdge.Bottom;
        var rect = StartPlacement.Compute(
            new PixelRect(area.X, area.Y, area.Width, area.Height), edge, _services.Settings.Current.StartPosition,
            TaskbarInfo.IconsAreCentered(_registry), ((int)(WidthDip * scale), (int)(HeightDip * scale)), (int)(GapDip * scale));
        AppWindow.MoveAndResize(new RectInt32(rect.X, rect.Y, rect.Width, rect.Height));
        AppWindow.ResizeClient(new SizeInt32(rect.Width, rect.Height));
    }

    // ---------------------------------------------------------- appearance

    private void ApplyAppearance()
    {
        var s = _services.Settings.Current;
        Root.RequestedTheme = s.StartTheme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        SystemBackdrop = s.StartBackdrop switch
        {
            "mica" => new MicaBackdrop(),
            "none" => null,
            _ => new DesktopAcrylicBackdrop(),
        };
        Root.Background = s.StartBackdrop == "none"
            ? (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"]
            : null;
    }

    // -------------------------------------------------------------- seeding

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _seedTimer;

    private void EnsureSeeded()
    {
        if (_services.StartLayout.Seeded) return;
        if (_services.Apps.Count == 0)
        {
            // First run, nothing cached yet: the installed apps are still being listed. Look again shortly.
            if (_seedTimer is null)
            {
                int tries = 0;
                _seedTimer = DispatcherQueue.CreateTimer();
                _seedTimer.Interval = TimeSpan.FromMilliseconds(500);
                _seedTimer.Tick += (t, _) =>
                {
                    if (_services.Apps.Count > 0 || ++tries > 40) t.Stop();
                    if (_services.Apps.Count > 0) EnsureSeeded();
                };
            }
            _seedTimer.Start();
            return;
        }
        var used = _services.Usage.Recent(40);
        string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        string downloads = Path.Combine(Folder(Environment.SpecialFolder.UserProfile), "Downloads");
        _services.StartLayout.SeedOnce(
            StartSeed.PinnedApps(_services.Apps.All, used),
            StartSeed.QuickLinks(Folder(Environment.SpecialFolder.MyDocuments), Directory.Exists(downloads) ? downloads : "", Folder(Environment.SpecialFolder.MyPictures)));
    }

    // --------------------------------------------------------------- views

    private void ResetView()
    {
        _showingAllApps = false;
        SearchBox.Text = "";
        ShowView(View.Pinned);
        PinnedList.SelectedIndex = -1;
        if (_pinned.Count > 0) PinnedList.ScrollIntoView(_pinned[0]);
    }

    private enum View { Pinned, Results, AllApps }

    private void ShowView(View view)
    {
        PinnedList.Visibility = view == View.Pinned ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = view == View.Pinned && _pinned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = view == View.Results ? Visibility.Visible : Visibility.Collapsed;
        AllAppsList.Visibility = view == View.AllApps ? Visibility.Visible : Visibility.Collapsed;
        NoResults.Visibility = Visibility.Collapsed;
        bool back = view == View.AllApps;
        AllAppsLabel.Text = back ? "Voltar" : "Todos os programas";
        AllAppsChevron.Glyph = back ? "\uE76B" : "\uE76C";
    }

    private void OnAllAppsClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        _showingAllApps = !_showingAllApps;
        if (_showingAllApps) LoadAllApps();
        ShowView(_showingAllApps ? View.AllApps : View.Pinned);
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void LoadAllApps()
    {
        _allApps.Clear();
        string last = "";
        foreach (var app in _services.Apps.All.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var vm = WithTasks(new ResultItemViewModel(StartSeed.AppResult(app)));
            string letter = LetterOf(app.Name);
            if (letter != last) { vm.Letter = letter; last = letter; }
            _allApps.Add(vm);
            if (vm.Result.Icon is { } icon) _ = LoadIconAsync(vm, icon);
        }
    }

    private static string LetterOf(string name)
    {
        if (name.Length == 0) return "#";
        // Strip accents so "Área de..." sits under A.
        string c = name[..1].Normalize(System.Text.NormalizationForm.FormD)[..1].ToUpperInvariant();
        return char.IsLetter(c[0]) ? c : "#";
    }

    // ------------------------------------------------------- pinned content

    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _rebuildQueued = false;
            RebuildPinned();
            if (_showingAllApps) LoadAllApps();
        });
    }

    /// <summary>The left list: the pinned apps (with the name of each section when there is more than one), then the most used ones.</summary>
    private void RebuildPinned()
    {
        var layout = _services.StartLayout.Snapshot();
        var settings = _services.Settings.Current;

        _pinned.Clear();
        foreach (var section in layout.Sections)
        {
            bool first = true;
            foreach (var item in section.Items)
            {
                var vm = WithTasks(new ResultItemViewModel(item.ToResult()));
                if (first && layout.Sections.Count > 1) vm.Header = section.Name;
                first = false;
                _pinned.Add(vm);
                if (item.Icon is { } icon) _ = LoadIconAsync(vm, icon);
            }
        }

        var pinnedKeys = layout.Sections.SelectMany(s => s.Items).Select(i => i.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recent = settings.StartShowRecent
            ? _services.Usage.Recent(RecentCount * 3)
                .Where(r => StartLayoutStore.CanPin(r) && !pinnedKeys.Contains(r.Key)).Take(RecentCount).ToList()
            : new List<SearchResult>();
        bool firstRecent = true;
        foreach (var r in recent)
        {
            var vm = WithTasks(new ResultItemViewModel(r));
            if (firstRecent && _pinned.Count > 0) vm.Header = "Usados recentemente";
            firstRecent = false;
            _pinned.Add(vm);
            if (r.Icon is { } icon) _ = LoadIconAsync(vm, icon);
        }

        if (PinnedList.Visibility == Visibility.Visible)
            EmptyHint.Visibility = _pinned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------- the right column

    private void BuildPlaces()
    {
        string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        string downloads = Path.Combine(Folder(Environment.SpecialFolder.UserProfile), "Downloads");

        AddPlace("\uE77B", Environment.UserName, () => OpenTarget(Folder(Environment.SpecialFolder.UserProfile)));
        AddPlace("\uE8A5", "Documentos", () => OpenTarget(Folder(Environment.SpecialFolder.MyDocuments)));
        AddPlace("\uEB9F", "Imagens", () => OpenTarget(Folder(Environment.SpecialFolder.MyPictures)));
        AddPlace("\uEC4F", "Músicas", () => OpenTarget(Folder(Environment.SpecialFolder.MyMusic)));
        AddPlace("\uE896", "Downloads", () => OpenTarget(downloads));
        AddSeparator();
        AddPlace("\uE823", "Itens recentes", () => OpenTarget("shell:Recent"));
        AddPlace("\uE7F4", "Este Computador", () => OpenTarget("shell:MyComputerFolder"));
        AddSeparator();
        AddPlace("\uE9F5", "Painel de Controle", () => OpenTarget("control.exe"));
        AddPlace("\uE713", "Configurações", () => OpenTarget("ms-settings:"));
        AddPlace("\uE895", "Windows Update", () => OpenTarget("ms-settings:windowsupdate"));
        AddPlace("\uE756", "Executar…", () => OpenTarget("explorer.exe", "shell:::{2559a1f3-21d7-11d4-bdaf-00c04f60b9f0}"));
        AddSeparator();
        AddPlace("\uE115", "Configurações do Apparatus", () =>
            _executor.Run(new SearchResult("command:settings", ResultKind.Command, "Configurações", "", InternalCommands.OpenSettings), "", secondary: false));
    }

    private void AddPlace(string glyph, string label, Action run)
    {
        var content = new Grid { ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, Opacity = 0.9 });
        var text = new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 9, 10, 9),
        };
        button.Click += (_, _) =>
        {
            HideMenu();   // first, so what is opened gets the foreground
            try { run(); }
            catch (Exception ex)
            {
                AppLog.Warn($"Opening “{label}” from the Start menu failed: {ex.Message}");
                _services.Host.ShowBalloon("Não foi possível abrir", $"{label}: {ex.Message}");
            }
        };
        PlacesPanel.Children.Add(button);
    }

    // ------------------------------------------------------- account picture

    private string? _avatarPath;

    /// <summary>Shows the picture of the signed-in Windows account in the round button (the person glyph when there is none).</summary>
    private void RefreshAvatar()
    {
        string? path = AccountPicturePath();
        if (path == _avatarPath) return;
        _avatarPath = path;
        try
        {
            if (path is null)
            {
                AvatarEllipse.Fill = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"];
                AvatarGlyph.Visibility = Visibility.Visible;
                return;
            }
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 128 };
            bitmap.UriSource = new Uri(path);
            AvatarEllipse.Fill = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
            AvatarGlyph.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Loading the account picture failed: {ex.Message}");
            AvatarGlyph.Visibility = Visibility.Visible;
        }
    }

    /// <summary>The picture file Windows keeps for this account (the AccountPicture key of HKLM, per user SID), or null.</summary>
    private static string? AccountPicturePath()
    {
        try
        {
            string? sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            if (sid is null) return null;
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\{sid}");
            if (key is null) return null;
            foreach (string name in new[] { "Image192", "Image208", "Image96", "Image448", "Image64", "Image240", "Image40", "Image32", "Image1080" })
                if (key.GetValue(name) is string file && File.Exists(file)) return file;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Reading the account picture failed: {ex.Message}");
        }
        return null;
    }

    private void OnAvatarClick(object sender, RoutedEventArgs e)
    {
        HideMenu();
        try { OpenTarget("ms-settings:yourinfo"); }
        catch (Exception ex) { AppLog.Warn($"Opening the account page failed: {ex.Message}"); }
    }

    private void AddSeparator() => PlacesPanel.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
    {
        Height = 1,
        Margin = new Thickness(10, 6, 10, 6),
        Fill = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
    });

    private static void OpenTarget(string target, string? arguments = null)
    {
        var info = new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true };
        if (arguments is not null) info.Arguments = arguments;
        System.Diagnostics.Process.Start(info);
    }

    /// <summary>Marks an app that has jump list tasks, so its row shows the button.</summary>
    private static ResultItemViewModel WithTasks(ResultItemViewModel vm)
    {
        if (vm.AppId is { } id) vm.HasTasks = JumpLists.HasTasks(id);
        return vm;
    }

    /// <summary>Replaces the generic icon of a task by its own, once loaded (the menu is already showing).</summary>
    private async Task ShowTaskIconAsync(MenuFlyoutItem item, string key)
    {
        try
        {
            if (await _icons.GetAsync(key) is { } source)
                item.Icon = new ImageIcon { Source = source, Width = 16, Height = 16 };
        }
        catch (Exception ex)
        {
            AppLog.Warn($"A task icon could not be loaded: {ex.Message}");
        }
    }

    private async void OnTasksClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ResultItemViewModel { AppId: { } appId } vm } button) return;
        try
        {
            var tasks = await JumpTaskReader.ReadAsync(appId);
            var menu = new MenuFlyout();
            if (tasks.Count == 0)
            {
                menu.Items.Add(new MenuFlyoutItem { Text = "Nenhuma tarefa disponível", IsEnabled = false });
            }
            foreach (var task in tasks)
            {
                var item = new MenuFlyoutItem { Text = task.Title, Icon = new FontIcon { Glyph = "\uE8A7" } };
                if (task.IconKey is { } iconKey) _ = ShowTaskIconAsync(item, iconKey);
                item.Click += (_, _) =>
                {
                    HideMenu();   // first, so the program being started gets the foreground
                    try { task.Run(); }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"Starting the task “{task.Title}” of {vm.Title} failed: {ex.Message}");
                        _services.Host.ShowBalloon("Não foi possível executar", $"{task.Title}: {ex.Message}");
                    }
                };
                menu.Items.Add(item);
            }
            menu.ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.RightEdgeAlignedTop });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Showing the tasks of {vm.Title} failed: {ex.Message}");
        }
    }

    private async Task LoadIconAsync(ResultItemViewModel vm, string key)
    {
        try
        {
            var image = await _icons.GetAsync(key);
            if (image is not null) vm.Icon = image;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Icon load failed for {key}: {ex.Message}");
        }
    }

    private void RunSelfChange(Action action)
    {
        _selfChange = true;
        try { action(); }
        finally { _selfChange = false; }
    }

    // ----------------------------------------------------------- power menu

    private void BuildPowerMenu()
    {
        void Add(string text, PowerCommand command)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) =>
            {
                HideMenu();
                try { PowerActions.Run(command); }
                catch (Exception ex)
                {
                    AppLog.Error($"Power command {command} failed", ex);
                    _services.Host.ShowBalloon("Não foi possível executar", ex.Message);
                }
            };
            PowerMenu.Items.Add(item);
        }
        Add("Bloquear", PowerCommand.Lock);
        Add("Sair da conta", PowerCommand.SignOut);
        PowerMenu.Items.Add(new MenuFlyoutSeparator());
        Add("Suspender", PowerCommand.Sleep);
        Add("Hibernar", PowerCommand.Hibernate);
        Add("Reiniciar", PowerCommand.Restart);
        Add("Desligar", PowerCommand.Shutdown);
    }

    // ------------------------------------------------------------ menus

    private enum Origin { Pinned, Recent, List }

    private MenuFlyout BuildItemMenu(ResultItemViewModel vm, Origin origin)
    {
        var r = vm.Result;
        var menu = new MenuFlyout();

        void Add(string text, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("Abrir", () => Execute(vm, secondary: false, query: ""));
        if (r.Kind is ResultKind.File or ResultKind.Folder) Add("Mostrar na pasta", () => Execute(vm, secondary: true, query: ""));
        menu.Items.Add(new MenuFlyoutSeparator());

        bool pinned = _services.StartLayout.IsPinned(r.Key);
        if (pinned)
        {
            Add("Desafixar do Iniciar", () => _services.StartLayout.Unpin(r.Key));

            var layout = _services.StartLayout.Snapshot();
            var current = layout.Sections.FirstOrDefault(s => s.Items.Any(i => string.Equals(i.Key, r.Key, StringComparison.OrdinalIgnoreCase)));
            var move = new MenuFlyoutSubItem { Text = "Mover para a seção" };
            foreach (var s in layout.Sections.Where(s => s.Id != current?.Id))
            {
                var item = new MenuFlyoutItem { Text = s.Name };
                string target = s.Id;
                item.Click += (_, _) => _services.StartLayout.Move(r.Key, target);
                move.Items.Add(item);
            }
            var fresh = new MenuFlyoutItem { Text = "Nova seção…" };
            fresh.Click += async (_, _) =>
            {
                var name = await AskTextAsync("Nova seção", "");
                if (name is null) return;
                string id = _services.StartLayout.AddSection(name);
                _services.StartLayout.Move(r.Key, id);
            };
            move.Items.Add(new MenuFlyoutSeparator());
            move.Items.Add(fresh);
            menu.Items.Add(move);
        }
        else
        {
            Add("Fixar no Iniciar", () => _services.StartLayout.Pin(r), StartLayoutStore.CanPin(r));
        }

        return menu;
    }

    private void OnRowContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ResultItemViewModel vm) return;
        ShowMenu(BuildItemMenu(vm, Origin.Pinned), sender, e);
    }

    private static void ShowMenu(MenuFlyout menu, UIElement anchor, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        var options = new FlyoutShowOptions();
        if (e.TryGetPosition(anchor, out var point)) options.Position = point;
        menu.ShowAt(anchor, options);
    }

    private async Task<string?> AskTextAsync(string title, string initial)
    {
        var box = new TextBox { Text = initial, MaxLength = 40, PlaceholderText = "Nome da seção" };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        _dialogOpen = true;
        try
        {
            box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // --------------------------------------------------------------- search

    private async void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        string text = SearchBox.Text;
        if (text.Length == 0)
        {
            _searchCts?.Cancel();
            ShowView(_showingAllApps ? View.AllApps : View.Pinned);
            return;
        }
        await RunSearchAsync(text);
    }

    private async Task RunSearchAsync(string text)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(15, cts.Token);
            var found = await _services.Search.SearchAsync(text, 12, cts.Token);
            if (cts.IsCancellationRequested) return;

            _results.Clear();
            foreach (var r in found)
            {
                var vm = WithTasks(new ResultItemViewModel(r));
                _results.Add(vm);
                if (r.Icon is { } icon) _ = LoadIconAsync(vm, icon);
            }
            ShowView(View.Results);
            NoResults.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_results.Count > 0) ResultsList.SelectedIndex = 0;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
        catch (Exception ex)
        {
            AppLog.Error("Start menu search failed", ex);
        }
    }

    // ------------------------------------------------------------ keyboard

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private ListView? ActiveList => ResultsList.Visibility == Visibility.Visible ? ResultsList
        : AllAppsList.Visibility == Visibility.Visible ? AllAppsList
        : PinnedList.Visibility == Visibility.Visible ? PinnedList : null;

    private void OnSearchPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsDown(VirtualKey.Control), shift = IsDown(VirtualKey.Shift);
        switch (e.Key)
        {
            case VirtualKey.Down or VirtualKey.Tab when !(e.Key == VirtualKey.Tab && shift):
                MoveSelection(+1);
                e.Handled = true;
                break;
            case VirtualKey.Up or VirtualKey.Tab:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                if (ActiveList?.SelectedItem is ResultItemViewModel vm) Execute(vm, secondary: ctrl, query: SearchBox.Text);
                e.Handled = true;
                break;
            case VirtualKey.P when ctrl:
                if (ActiveList?.SelectedItem is ResultItemViewModel pin) TogglePin(pin);
                e.Handled = true;
                break;
        }
    }

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            if (SearchBox.Text.Length > 0) SearchBox.Text = "";
            else if (_showingAllApps) OnAllAppsClick(this, new RoutedEventArgs());
            else HideMenu();
            e.Handled = true;
            return;
        }

        // Typing while a tile has the focus starts a search instead of being lost.
        bool printable = e.Key is >= VirtualKey.A and <= VirtualKey.Z or >= VirtualKey.Number0 and <= VirtualKey.Number9;
        if (printable && !IsDown(VirtualKey.Control) && !IsDown(VirtualKey.Menu) && FocusManager.GetFocusedElement(Root.XamlRoot) is not TextBox)
            SearchBox.Focus(FocusState.Keyboard);
    }

    private void MoveSelection(int delta)
    {
        if (ActiveList is not { } list || list.Items.Count == 0) return;
        int next = Math.Clamp(list.SelectedIndex + delta, 0, list.Items.Count - 1);
        list.SelectedIndex = next;
        list.ScrollIntoView(list.Items[next]);
    }

    // ------------------------------------------------------------- actions

    private void OnRowClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ResultItemViewModel vm) Execute(vm, secondary: false, query: ResultsList.Visibility == Visibility.Visible ? SearchBox.Text : "");
    }

    private void Execute(ResultItemViewModel vm, bool secondary, string query)
    {
        HideMenu();   // first, so the program being launched gets the foreground
        _executor.Run(vm.Result, query, secondary);
    }

    private void TogglePin(ResultItemViewModel vm)
    {
        var store = _services.StartLayout;
        if (store.IsPinned(vm.Result.Key)) store.Unpin(vm.Result.Key);
        else store.Pin(vm.Result);
    }
}
