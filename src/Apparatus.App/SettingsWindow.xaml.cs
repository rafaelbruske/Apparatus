using Apparatus.Core.Actions;
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
        new("Geral", "", "general"),
        new("Pesquisa", "", "search"),
        new("Backup e sincronização", "", "backup"),
        new("Atualizações e dados", "", "about"),
        new("Sobre", "", "aboutapp"),

        new("Módulos", null, null),
        new("Snippets", "", SnippetsPage),
        new("Ações e atalhos", "", ActionsPage),
        new("Ajustes dos snippets", "", "snippets"),
        new("Área de transferência", "", "clipboard"),
        new("Captura de tela", "", "capture"),
        new("Extensões", "", "extensions"),

        new("Menu Iniciar", null, null),
        new("Menu Iniciar do Apparatus", "", "start"),

        new("Assistente de IA", null, null),
        new("Conexão e comportamento", "", "assistant"),

        new("Windows", null, null),
        new("Barra de tarefas e Iniciar", "", "windows"),
    };

    public SettingsWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon(AppPaths.AssetPath("apparatus.ico"));
        PlaceOnActiveMonitor();

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

    // ------------------------------------------------------------ showing

    /// <summary>Brings the window to the front on the given page. <paramref name="after"/> runs once the page is shown.</summary>
    public void ShowPage(string page, Action<SettingsWindow>? after = null)
    {
        _afterPageShown = after is null ? null : () => after(this);
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
        WindowHelpers.BringToForeground(_hwnd);
        SelectPage(page);
    }

    /// <summary>Opens the snippets page; <paramref name="startNew"/> also starts a new snippet (from <paramref name="draft"/> when given).</summary>
    public void ShowSnippets(bool startNew = false, Snippet? draft = null) =>
        ShowPage(SnippetsPage, startNew ? w => w._snippetsView?.StartNewSnippet(draft) : null);

    /// <summary>Opens the actions page; <paramref name="startNew"/> also starts a new action (from <paramref name="draft"/> when given).</summary>
    public void ShowActions(bool startNew = false, ActionDefinition? draft = null) =>
        ShowPage(ActionsPage, startNew ? w => w._actionsView?.StartNewAction(draft) : null);

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
        switch (page)
        {
            case SnippetsPage:
                _snippetsView ??= new SnippetsView(_services, _hwnd);
                PanelContent.Content = _snippetsView;
                break;
            case ActionsPage:
                _actionsView ??= new ActionsView(_services, _hwnd)
                {
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

    private void RunAfterPageShown()
    {
        var action = _afterPageShown;
        _afterPageShown = null;
        action?.Invoke();
    }
}
