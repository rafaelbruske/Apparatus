using System.Collections.ObjectModel;
using System.Globalization;
using Apparatus.Core;
using Apparatus.Core.Search;
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
/// Apparatus' own Start menu: pinned items in named sections, the most used items, an alphabetical list of every app,
/// inline search over the whole launcher index, quick shortcuts and the power menu.
/// </summary>
public sealed partial class StartMenuWindow : Window
{
    private const double WidthDip = 640;
    private const double HeightDip = 740;
    private const double GapDip = 12;
    private const double ContentWidthDip = 584;
    private const int RecentCount = 6;

    private readonly AppServices _services;
    private readonly ResultExecutor _executor;
    private readonly IconCache _icons;
    private readonly nint _hwnd;
    private readonly RegistryTweakStore _registry = new();
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
        ResultsList.ItemsSource = _results;
        AllAppsList.ItemsSource = _allApps;

        ExtendsContentIntoTitleBar = true;
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
        AppWindow.MoveAndResize(new RectInt32(-12000, -12000, 640, 740));
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
        PinnedScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    private enum View { Pinned, Results, AllApps }

    private void ShowView(View view)
    {
        PinnedScroll.Visibility = view == View.Pinned ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = view == View.Results ? Visibility.Visible : Visibility.Collapsed;
        AllAppsList.Visibility = view == View.AllApps ? Visibility.Visible : Visibility.Collapsed;
        NoResults.Visibility = Visibility.Collapsed;
        AllAppsLabel.Text = view == View.AllApps ? "‹  Voltar" : "Todos os aplicativos  ›";
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
            var vm = new ResultItemViewModel(StartSeed.AppResult(app));
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

    private void RebuildPinned()
    {
        var layout = _services.StartLayout.Snapshot();
        var settings = _services.Settings.Current;
        int columns = Math.Clamp(settings.StartColumns, 4, 8);
        double itemWidth = Math.Floor(ContentWidthDip / columns);

        SectionsPanel.Children.Clear();
        foreach (var section in layout.Sections) SectionsPanel.Children.Add(BuildSection(section, layout.Sections, itemWidth));
        EmptyHint.Visibility = layout.Sections.All(s => s.Items.Count == 0) ? Visibility.Visible : Visibility.Collapsed;

        BuildRecents(layout, itemWidth, settings.StartShowRecent);
        BuildQuickBar(layout);
    }

    private UIElement BuildSection(StartSection section, IReadOnlyList<StartSection> all, double itemWidth)
    {
        var items = new ObservableCollection<ResultItemViewModel>();
        foreach (var item in section.Items)
        {
            var vm = new ResultItemViewModel(item.ToResult());
            items.Add(vm);
            if (item.Icon is { } icon) _ = LoadIconAsync(vm, icon);
        }

        var title = new TextBlock { Text = section.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
        var more = new Button
        {
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            Flyout = BuildSectionMenu(section, all),
        };
        ToolTipService.SetToolTip(more, "Opções da seção");
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(more, 1);
        header.Children.Add(more);

        var grid = new GridView
        {
            ItemsSource = items,
            ItemTemplate = (DataTemplate)Root.Resources["TileTemplate"],
            ItemContainerStyle = (Style)Root.Resources["TileContainerStyle"],
            ItemsPanel = TilePanel(itemWidth),
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            CanReorderItems = true,
            CanDragItems = true,
            AllowDrop = true,
            MinHeight = 44,
            Tag = section.Id,
        };
        ScrollViewer.SetVerticalScrollMode(grid, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Hidden);
        grid.ItemClick += OnTileClick;
        grid.ContextRequested += OnTileContextRequested;
        grid.PointerWheelChanged += OnInnerWheel;
        grid.DragItemsStarting += (_, e) =>
        {
            if (e.Items.Count == 1 && e.Items[0] is ResultItemViewModel vm) e.Data.SetText(vm.Result.Key);
            e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        };
        grid.DragItemsCompleted += (s, _) =>
        {
            if (s is not GridView g || g.ItemsSource is not ObservableCollection<ResultItemViewModel> list) return;
            RunSelfChange(() => _services.StartLayout.SetOrder((string)g.Tag, list.Select(i => i.Result.Key).ToList()));
        };
        grid.DragOver += (_, e) => e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        grid.Drop += OnTileDrop;

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(header);
        panel.Children.Add(grid);
        return panel;
    }

    /// <summary>A tile dropped from another section moves there. Reordering inside one section is done by the GridView itself.</summary>
    private async void OnTileDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (sender is not GridView g || g.ItemsSource is not ObservableCollection<ResultItemViewModel> list) return;
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;
            string key = await e.DataView.GetTextAsync();
            if (list.Any(i => string.Equals(i.Result.Key, key, StringComparison.OrdinalIgnoreCase))) return;   // same section
            _services.StartLayout.Move(key, (string)g.Tag);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Dropping a Start tile failed: {ex.Message}");
        }
    }

    private void OnInnerWheel(object sender, PointerRoutedEventArgs e)
    {
        // The tile grids do not scroll by themselves; hand the wheel to the page.
        int delta = e.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;
        PinnedScroll.ChangeView(null, PinnedScroll.VerticalOffset - delta, null, disableAnimation: true);
        e.Handled = true;
    }

    private static ItemsPanelTemplate TilePanel(double itemWidth) => (ItemsPanelTemplate)XamlReader.Load(
        "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
        $"<ItemsWrapGrid Orientation='Horizontal' ItemWidth='{itemWidth.ToString(CultureInfo.InvariantCulture)}' ItemHeight='96' />" +
        "</ItemsPanelTemplate>");

    private void BuildRecents(StartLayout layout, double itemWidth, bool show)
    {
        var pinned = layout.Sections.SelectMany(s => s.Items).Select(i => i.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recent = show
            ? _services.Usage.Recent(RecentCount * 3)
                .Where(r => StartLayoutStore.CanPin(r) && !pinned.Contains(r.Key)).Take(RecentCount).ToList()
            : new List<SearchResult>();

        var items = new ObservableCollection<ResultItemViewModel>();
        foreach (var r in recent)
        {
            var vm = new ResultItemViewModel(r);
            items.Add(vm);
            if (r.Icon is { } icon) _ = LoadIconAsync(vm, icon);
        }
        RecentsGrid.ItemsSource = items;
        RecentsGrid.ItemsPanel = TilePanel(itemWidth);
        var visible = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentsHeader.Visibility = visible;
        RecentsGrid.Visibility = visible;
        RecentsGrid.Tag = "recent";
        RecentsGrid.PointerWheelChanged -= OnInnerWheel;
        RecentsGrid.PointerWheelChanged += OnInnerWheel;
    }

    private void BuildQuickBar(StartLayout layout)
    {
        QuickBar.Children.Clear();
        foreach (var q in layout.Quick)
        {
            var vm = new ResultItemViewModel(q.ToResult());
            var glyph = new FontIcon { Glyph = vm.Glyph, FontSize = 18, Opacity = 0.85 };
            var image = new Image { Width = 20, Height = 20, Visibility = Visibility.Collapsed };
            var content = new Grid { Width = 22, Height = 22 };
            content.Children.Add(glyph);
            content.Children.Add(image);

            var button = new Button
            {
                Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
                Content = content,
                Padding = new Thickness(8),
                Tag = vm,
            };
            ToolTipService.SetToolTip(button, q.Title);
            button.Click += (_, _) => Execute(vm, secondary: false, query: "");

            var remove = new MenuFlyoutItem { Text = "Remover da barra" };
            remove.Click += (_, _) => _services.StartLayout.RemoveQuick(q.Key);
            button.ContextFlyout = new MenuFlyout { Items = { remove } };
            QuickBar.Children.Add(button);

            if (q.Icon is { } icon) _ = ShowIconAsync(icon, image, glyph);
        }
    }

    private async Task ShowIconAsync(string key, Image image, FontIcon glyph)
    {
        try
        {
            var source = await _icons.GetAsync(key);
            if (source is null) return;
            image.Source = source;
            image.Visibility = Visibility.Visible;
            glyph.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Icon load failed for {key}: {ex.Message}");
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

    private MenuFlyout BuildSectionMenu(StartSection section, IReadOnlyList<StartSection> all)
    {
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "Renomear…" };
        rename.Click += async (_, _) =>
        {
            var name = await AskTextAsync("Renomear seção", section.Name);
            if (name is not null) _services.StartLayout.RenameSection(section.Id, name);
        };
        var up = new MenuFlyoutItem { Text = "Mover para cima", IsEnabled = all.Count > 1 && all[0].Id != section.Id };
        up.Click += (_, _) => _services.StartLayout.MoveSection(section.Id, -1);
        var down = new MenuFlyoutItem { Text = "Mover para baixo", IsEnabled = all.Count > 1 && all[^1].Id != section.Id };
        down.Click += (_, _) => _services.StartLayout.MoveSection(section.Id, +1);
        var delete = new MenuFlyoutItem { Text = "Excluir seção", IsEnabled = all.Count > 1 };
        delete.Click += (_, _) => _services.StartLayout.RemoveSection(section.Id);
        menu.Items.Add(rename);
        menu.Items.Add(up);
        menu.Items.Add(down);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(delete);
        return menu;
    }

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

        Add("Adicionar à barra rápida", () => _services.StartLayout.AddQuick(r), StartLayoutStore.CanPin(r));
        return menu;
    }

    private void OnTileContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not GridView grid || (e.OriginalSource as FrameworkElement)?.DataContext is not ResultItemViewModel vm) return;
        ShowMenu(BuildItemMenu(vm, grid == RecentsGrid ? Origin.Recent : Origin.Pinned), grid, e);
    }

    private void OnRowContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ResultItemViewModel vm) return;
        ShowMenu(BuildItemMenu(vm, Origin.List), sender, e);
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
                var vm = new ResultItemViewModel(r);
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
        : AllAppsList.Visibility == Visibility.Visible ? AllAppsList : null;

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
        if (ActiveList is { } list)
        {
            if (list.Items.Count == 0) return;
            int next = Math.Clamp(list.SelectedIndex + delta, 0, list.Items.Count - 1);
            list.SelectedIndex = next;
            list.ScrollIntoView(list.Items[next]);
            return;
        }

        // Pinned view: Down moves into the first tile.
        if (delta > 0 && SectionsPanel.Children.Count > 0 && FirstGrid() is { } grid)
        {
            if (grid.SelectedIndex < 0) grid.SelectedIndex = 0;
            grid.Focus(FocusState.Keyboard);
        }
    }

    private GridView? FirstGrid() => SectionsPanel.Children.OfType<StackPanel>()
        .Select(p => p.Children.OfType<GridView>().FirstOrDefault()).FirstOrDefault(g => g is { Items.Count: > 0 });

    // ------------------------------------------------------------- actions

    private void OnTileClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ResultItemViewModel vm) Execute(vm, secondary: false, query: "");
    }

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

    private async void OnNewSectionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = await AskTextAsync("Nova seção", "");
            if (name is not null) _services.StartLayout.AddSection(name);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Creating a Start section failed: {ex.Message}");
        }
    }
}
