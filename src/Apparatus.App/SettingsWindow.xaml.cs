using Apparatus.Core;
using Apparatus.Core.Actions;
using Apparatus.Core.Settings;
using Apparatus.Core.Snippets;
using Apparatus.Native;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace Apparatus.App;

/// <summary>
/// The Apparatus window for everything that is configured: a side bar of groups plus a page. The pages are the settings
/// (<see cref="SettingsView"/>), the snippets and the actions. It is an ordinary Windows window (title bar, minimize,
/// maximize, own entry in the taskbar), separate from the launcher. Leaving a page with unsaved edits, or closing the window,
/// asks first.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private const double WidthDip = 1280;
    private const double HeightDip = 780;
    private const string SnippetsPage = "snippets-list", ActionsPage = "actions-list";

    private readonly AppServices _services;
    private readonly nint _hwnd;
    private bool _navBusy;
    private bool _allowClose;
    private bool _closeAsking;
    private bool _macroHidden;
    private string _page = "";
    private Action? _afterPageShown;
    private readonly SettingsView _settingsView;
    private SnippetsView? _snippetsView;
    private ActionsView? _actionsView;

    /// <summary>One line of the side bar: a heading (no <see cref="Page"/>) or a page.</summary>
    private sealed record NavEntry(string Label, string? Glyph, string? Page);

    private static readonly NavEntry[] Navigation =
    {
        new("Apparatus", null, null),
        new("Geral", "\uE713", "general"),
        new("Pesquisa", "\uE721", "search"),
        new("Backup e sincronização", "\uE895", "backup"),
        new("Atualizações e dados", "\uE777", "about"),
        new("Sobre", "\uE946", "aboutapp"),

        new("Módulos", null, null),
        new("Snippets", "\uE8A5", SnippetsPage),
        new("Ações e atalhos", "\uE768", ActionsPage),
        new("Ajustes dos snippets", "\uE70F", "snippets"),
        new("Área de transferência", "\uE8C8", "clipboard"),
        new("Captura de tela", "\uE722", "capture"),
        new("Extensões", "\uE7B8", "extensions"),

        new("Menu Iniciar", null, null),
        new("Menu Iniciar do Apparatus", "\uE80F", "start"),

        new("Assistente de IA", null, null),
        new("Conexão e comportamento", "\uE8BD", "assistant"),

        new("Windows", null, null),
        new("Barra de tarefas e Iniciar", "\uE7F4", "windows"),
    };

    public SettingsWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon(AppPaths.AssetPath("apparatus.ico"));
        PlaceOnActiveMonitor();
        UseModernTitleBar();
        var find = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.F, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        find.Invoked += (_, e) => { SearchBox.Focus(FocusState.Keyboard); e.Handled = true; };
        Root.KeyboardAccelerators.Add(find);
        Root.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;   // or "Ctrl+F" pops up wherever the mouse rests

        _settingsView = new SettingsView(services, _hwnd);
        BuildNavigation();

        AppWindow.Closing += OnClosing;
        Closed += (_, _) =>
        {
            PanelContent.Content = null;
            _settingsView.Close();
            _snippetsView?.Close();
            _actionsView?.Close();
        };
    }

    // -------------------------------------------------------------- search

    /// <summary>Words that find the snippets and actions pages, which are editors and not cards of settings.</summary>
    private const string SnippetsKeywords = "snippets texto gatilho categoria novo importar exportar variaveis",
        ActionsKeywords = "acoes atalhos atalho teclado passos macro gravar novo importar exportar";

    private readonly Dictionary<string, TextBlock> _navCounts = new();
    private string _query = "";

    /// <summary>
    /// The search box filters the side bar to the pages that have a setting with the words typed (with how many), and each of those
    /// pages then shows only the blocks that have them. Enter opens the first page, Down moves into the side bar, Esc clears.
    /// Typing only moves between settings pages; the snippets and actions pages are opened by Enter or a click. Opening them
    /// ends the search, as does a request from outside to show a page: a page must never come up emptied by an old search.
    /// </summary>
    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) ApplySearch(sender.Text);
    }

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (FirstVisiblePage() is { } page) SelectPage(page);
    }

    private void OnSearchKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Down:
                // Into the side bar, on the page being shown.
                if (PanelNav.SelectedItem is ListViewItem selected && selected.Visibility == Visibility.Visible) selected.Focus(FocusState.Keyboard);
                else if (FirstVisibleItem() is { } first) first.Focus(FocusState.Keyboard);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Escape when SearchBox.Text.Length > 0:
                SearchBox.Text = "";
                ApplySearch("");
                e.Handled = true;
                break;
        }
    }

    private void ApplySearch(string text)
    {
        _query = text.Trim();
        var counts = _settingsView.ApplyFilter(_query);
        var words = SettingsSearch.Words(_query);

        bool anyInGroup = false;
        ListViewItem? heading = null;
        foreach (var item in PanelNav.Items.OfType<ListViewItem>())
        {
            if (item.Tag is not NavEntry entry) continue;
            if (entry.Page is null)
            {
                if (heading is not null) heading.Visibility = words.Count == 0 || anyInGroup ? Visibility.Visible : Visibility.Collapsed;
                heading = item;
                anyInGroup = false;
                continue;
            }

            bool match;
            int count = 0;
            if (words.Count == 0) match = true;
            else if (entry.Page == SnippetsPage) match = SettingsSearch.Matches(entry.Label + " " + SnippetsKeywords, words);
            else if (entry.Page == ActionsPage) match = SettingsSearch.Matches(entry.Label + " " + ActionsKeywords, words);
            else match = (count = counts.GetValueOrDefault(entry.Page)) > 0;

            item.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            anyInGroup |= match;
            if (_navCounts.TryGetValue(entry.Page, out var badge)) badge.Text = words.Count > 0 && count > 0 ? count.ToString() : "";
        }
        if (heading is not null) heading.Visibility = words.Count == 0 || anyInGroup ? Visibility.Visible : Visibility.Collapsed;

        // When no setting has the words, the page on the screen is empty: say why, and how to open an editor page that matched.
        string? note = null;
        if (words.Count > 0 && counts.Count == 0)
        {
            note = $"Nenhuma configuração com “{_query}”.";
            if (FirstVisibleItem()?.Tag is NavEntry editor) note += $" Pressione Enter para abrir {editor.Label}.";
        }
        _settingsView.ShowSearchNote(note);

        // The page on the screen has nothing to show: move to the first settings page that has. (Not from the snippets and actions
        // pages: leaving them can ask about unsaved edits, which would be rude in the middle of typing.)
        if (words.Count > 0 && _page != SnippetsPage && _page != ActionsPage
            && SettingsSearch.PageToJumpTo(_page, Navigation.Select(n => n.Page).OfType<string>(), counts) is { } target)
        {
            SelectPage(target);
        }
    }

    private void ClearSearch()
    {
        if (_query.Length == 0 && SearchBox.Text.Length == 0) return;
        SearchBox.Text = "";
        ApplySearch("");
    }

    private ListViewItem? FirstVisibleItem() =>
        PanelNav.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Visibility == Visibility.Visible && i.Tag is NavEntry { Page: not null });

    private string? FirstVisiblePage() => (FirstVisibleItem()?.Tag as NavEntry)?.Page;

    // ---------------------------------------------------------- title bar

    /// <summary>
    /// The Fluent look: the title bar is part of the window (same Mica background), with the icon and the title on the left and
    /// the system buttons on the right without a colour of their own.
    /// </summary>
    private void UseModernTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var bar = AppWindow.TitleBar;
        var clear = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        bar.ButtonBackgroundColor = clear;
        bar.ButtonInactiveBackgroundColor = clear;

        try { TitleIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(AppPaths.AssetPath("apparatus.ico"))); }
        catch (Exception ex) { AppLog.Warn($"The title bar icon could not be loaded: {ex.Message}"); }

        // The system buttons sit on the right: leave their width free of the draggable area's content.
        AppTitleBar.Loaded += (_, _) => UpdateTitleBarInsets();
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarInsets();
    }

    private void UpdateTitleBarInsets()
    {
        double scale = WindowHelpers.GetScale(_hwnd);
        TitleBarRightInset.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
    }

    // ------------------------------------------------------------ showing

    /// <summary>Brings the window to the front on the given page. <paramref name="after"/> runs once the page is shown.</summary>
    public void ShowPage(string page, Action<SettingsWindow>? after = null)
    {
        _afterPageShown = after is null ? null : () => after(this);
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
        WindowHelpers.BringToForeground(_hwnd);
        ClearSearch();
        SelectPage(page);
    }

    /// <summary>Opens the snippets page; <paramref name="startNew"/> also starts a new snippet (from <paramref name="draft"/> when given).</summary>
    public void ShowSnippets(bool startNew = false, Snippet? draft = null) =>
        ShowPage(SnippetsPage, startNew ? w => w._snippetsView?.StartNewSnippet(draft) : null);

    /// <summary>Opens the actions page; <paramref name="startNew"/> also starts a new action (from <paramref name="draft"/> when given).</summary>
    public void ShowActions(bool startNew = false, ActionDefinition? draft = null) =>
        ShowPage(ActionsPage, startNew ? w => w._actionsView?.StartNewAction(draft) : null);

    /// <summary>True while a macro is being recorded: the window is hidden on purpose and the launcher must stay out of the way.</summary>
    public bool IsRecordingMacro => _macroHidden;

    /// <summary>Gets the window out of the way (before a screen capture).</summary>
    public void Minimize()
    {
        if (AppWindow.Presenter is OverlappedPresenter p) p.Minimize();
    }

    /// <summary>Closes the window without asking (the app is quitting).</summary>
    public void CloseNow()
    {
        _allowClose = true;
        Close();
    }

    private void PlaceOnActiveMonitor()
    {
        var (cx, cy) = WindowHelpers.CursorPosition();
        var area = DisplayArea.GetFromPoint(new PointInt32(cx, cy), DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Move(new PointInt32(area.X + 40, area.Y + 40));   // so the window takes that monitor's DPI before it is sized
        double scale = WindowHelpers.GetScale(_hwnd);
        int width = Math.Min((int)(WidthDip * scale), area.Width - 40);
        int height = Math.Min((int)(HeightDip * scale), area.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    // ------------------------------------------------------------ closing

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closeAsking) return;
        _ = AskThenCloseAsync();
    }

    private async Task AskThenCloseAsync()
    {
        _closeAsking = true;
        try
        {
            if (await ConfirmLeaveCurrentPageAsync()) CloseNow();
        }
        finally
        {
            _closeAsking = false;
        }
    }

    /// <summary>True when it is fine to leave the page now (nothing unsaved, or the user saved or discarded).</summary>
    private async Task<bool> ConfirmLeaveCurrentPageAsync() => _page switch
    {
        SnippetsPage when _snippetsView is not null => await _snippetsView.ConfirmLeaveAsync(),
        ActionsPage when _actionsView is not null => await _actionsView.ConfirmLeaveAsync(),
        _ => true,
    };

    // ------------------------------------------------------------ side bar

    private void BuildNavigation()
    {
        _navBusy = true;
        PanelNav.Items.Clear();
        foreach (var entry in Navigation)
        {
            if (entry.Page is null)
            {
                // A heading: not something to pick.
                PanelNav.Items.Add(new ListViewItem
                {
                    Content = new TextBlock { Text = entry.Label, FontSize = 11, Opacity = 0.55, Margin = new Thickness(2, 10, 0, 2) },
                    Tag = entry,
                    IsHitTestVisible = false,
                    IsTabStop = false,
                    MinHeight = 28,
                });
                continue;
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(new FontIcon { Glyph = entry.Glyph ?? "", FontSize = 15, Opacity = 0.85 });
            row.Children.Add(new TextBlock { Text = entry.Label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            // How many blocks of the page have the words being searched for (empty when nothing is being searched).
            var badge = new TextBlock { FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(badge);
            _navCounts[entry.Page] = badge;
            var item = new ListViewItem { Content = row, Tag = entry, MinHeight = 38 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, entry.Label);
            PanelNav.Items.Add(item);
        }
        _navBusy = false;
    }

    private void SelectPage(string page)
    {
        if (_page == page) { RunAfterPageShown(); return; }
        foreach (var item in PanelNav.Items.OfType<ListViewItem>())
        {
            if (item.Tag is NavEntry { Page: { } p } && p == page)
            {
                PanelNav.SelectedItem = item;   // OnPanelNavChanged shows it
                return;
            }
        }
    }

    private async void OnPanelNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navBusy) return;
        var previous = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
        if (PanelNav.SelectedItem is not ListViewItem { Tag: NavEntry { Page: { } page } } || page == _page)
        {
            // A heading (reachable by keyboard): keep the previous choice.
            if (PanelNav.SelectedItem is ListViewItem { Tag: NavEntry { Page: null } }) RevertNavigation(previous);
            return;
        }

        if (!await ConfirmLeaveCurrentPageAsync())
        {
            RevertNavigation(previous);
            _afterPageShown = null;
            return;
        }
        ShowPanelPage(page);
    }

    private void RevertNavigation(object? previous)
    {
        _navBusy = true;
        PanelNav.SelectedItem = previous;
        _navBusy = false;
    }

    private void ShowPanelPage(string page)
    {
        _page = page;
        if (page is SnippetsPage or ActionsPage) ClearSearch();   // the search box goes away with the side bar
        UpdateNavigationVisibility(page);
        switch (page)
        {
            case SnippetsPage:
                _snippetsView ??= new SnippetsView(_services, _hwnd) { BackRequested = GoBackToSettings };
                PanelContent.Content = _snippetsView;
                break;
            case ActionsPage:
                _actionsView ??= new ActionsView(_services, _hwnd)
                {
                    BackRequested = GoBackToSettings,
                    RecordingStarted = () => { _macroHidden = true; AppWindow.Hide(); },
                    RecordingFinished = () =>
                    {
                        _macroHidden = false;
                        AppWindow.Show();
                        Activate();
                        WindowHelpers.BringToForeground(_hwnd);
                    },
                };
                PanelContent.Content = _actionsView;
                break;
            default:
                PanelContent.Content = _settingsView;
                _settingsView.ShowPage(page);
                break;
        }
        RunAfterPageShown();
    }

    /// <summary>
    /// Snippets and actions take the whole window: the side bar of the settings goes away and the list of the page has a "Voltar"
    /// button at the top. The settings pages bring the side bar back.
    /// </summary>
    private void UpdateNavigationVisibility(string page)
    {
        bool manager = page == SnippetsPage || page == ActionsPage;
        if (!manager) _lastSettingsPage = page;
        NavPanel.Visibility = manager ? Visibility.Collapsed : Visibility.Visible;
        NavColumn.Width = manager ? new GridLength(0) : new GridLength(244);
    }

    private string _lastSettingsPage = "general";

    private void GoBackToSettings() => SelectPage(_lastSettingsPage);

    private void RunAfterPageShown()
    {
        var action = _afterPageShown;
        _afterPageShown = null;
        action?.Invoke();
    }
}
