using Apparatus.Core.Actions;
using Apparatus.Core.Snippets;
using Apparatus.Native;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;

namespace Apparatus.App;

/// <summary>
/// The settings panel. The launcher window grows into a side bar (the groups) plus a page, and goes back to the search when it is left.
/// The pages are the settings, the snippets and the actions. While the panel is open the window does not hide when it loses the focus:
/// a half-made change must not vanish because the user clicked elsewhere, and leaving a page with unsaved edits asks first.
/// </summary>
public sealed partial class LauncherWindow
{
    private const double PanelWidthDip = 1280;
    private const double PanelHeightDip = 780;
    private const string SnippetsPage = "snippets-list", ActionsPage = "actions-list";

    private bool _panelMode;
    private bool _panelNavBusy;
    private bool _macroHidden;
    private string _panelPage = "";
    private Action? _afterPageShown;
    private SettingsView? _settingsView;
    private SnippetsView? _snippetsView;
    private ActionsView? _actionsView;

    /// <summary>One line of the side bar: a heading (no <see cref="Page"/>) or a page.</summary>
    private sealed record NavEntry(string Label, string? Glyph, string? Page);

    private static readonly NavEntry[] Navigation =
    {
        new("Apparatus", null, null),
        new("Geral", "", "general"),
        new("Pesquisa", "", "search"),
        new("Backup e sincronização", "", "backup"),
        new("Atualizações e dados", "", "about"),
        new("Sobre", "", "aboutapp"),

        new("Módulos", null, null),
        new("Snippets", "", SnippetsPage),
        new("Ações e atalhos", "", ActionsPage),
        new("Ajustes dos snippets", "", "snippets"),
        new("Área de transferência", "", "clipboard"),
        new("Captura de tela", "", "capture"),
        new("Extensões", "", "extensions"),

        new("Menu Iniciar", null, null),
        new("Menu Iniciar do Apparatus", "", "start"),

        new("Assistente de IA", null, null),
        new("Conexão e comportamento", "", "assistant"),

        new("Windows", null, null),
        new("Barra de tarefas e Iniciar", "", "windows"),
    };

    /// <summary>True while the panel replaces the search.</summary>
    public bool IsPanelOpen => _panelMode;

    /// <summary>Opens the snippets page; <paramref name="startNew"/> also starts a new snippet (from <paramref name="draft"/> when given).</summary>
    public void ShowSnippets(bool startNew = false, Snippet? draft = null)
    {
        _afterPageShown = startNew ? () => _snippetsView?.StartNewSnippet(draft) : null;
        ShowLauncher(panelPage: SnippetsPage);
    }

    /// <summary>Opens the actions page; <paramref name="startNew"/> also starts a new action (from <paramref name="draft"/> when given).</summary>
    public void ShowActions(bool startNew = false, ActionDefinition? draft = null)
    {
        _afterPageShown = startNew ? () => _actionsView?.StartNewAction(draft) : null;
        ShowLauncher(panelPage: ActionsPage);
    }

    /// <summary>Opens the panel on a page (or shows that page when it is already open).</summary>
    private void OpenSettingsPanel(string page)
    {
        if (_panelMode) SelectPanelPage(page);
        else EnterPanel(page);
    }

    private void EnterPanel(string page)
    {
        if (_panelMode) { SelectPanelPage(page); return; }
        if (_aiMode) LeaveAiVisuals();
        _searchCts?.Cancel();
        _panelMode = true;
        _panelPage = "";

        SearchRow.Visibility = Visibility.Collapsed;
        Divider.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Collapsed;
        Footer.Visibility = Visibility.Collapsed;
        PanelHost.Visibility = Visibility.Visible;

        _settingsView = new SettingsView(_services, _hwnd);
        BuildNavigation();
        PositionPanel();
        SelectPanelPage(page);
        DispatcherQueue.TryEnqueue(() => PanelNav.Focus(FocusState.Programmatic));
    }

    /// <summary>Puts the launcher window back as it was before the panel. <paramref name="search"/>: also bring the search back.</summary>
    private void LeavePanel(bool search)
    {
        if (!_panelMode) return;
        _panelMode = false;
        _panelPage = "";
        _afterPageShown = null;
        PanelHost.Visibility = Visibility.Collapsed;
        PanelContent.Content = null;
        _settingsView?.Close();
        _snippetsView?.Close();
        _actionsView?.Close();
        _settingsView = null;
        _snippetsView = null;
        _actionsView = null;
        SearchRow.Visibility = Visibility.Visible;
        if (!search) return;

        PositionOnActiveMonitor();
        LeaveAiVisuals();
        _ = RunSearchAsync(SearchBox.Text);
        DispatcherQueue.TryEnqueue(() => SearchBox.Focus(FocusState.Programmatic));
    }

    /// <summary>Leaves the panel for the search, asking first when the page being left has unsaved edits.</summary>
    private async Task TryLeavePanelAsync()
    {
        if (await ConfirmLeaveCurrentPageAsync()) LeavePanel(search: true);
    }

    /// <summary>True when it is fine to leave the page now (nothing unsaved, or the user saved or discarded).</summary>
    private async Task<bool> ConfirmLeaveCurrentPageAsync() => _panelPage switch
    {
        SnippetsPage when _snippetsView is not null => await _snippetsView.ConfirmLeaveAsync(),
        ActionsPage when _actionsView is not null => await _actionsView.ConfirmLeaveAsync(),
        _ => true,
    };

    /// <summary>The hotkey while the panel has the focus: close the launcher, after the same question about unsaved edits.</summary>
    private async Task ConfirmThenHideAsync()
    {
        if (await ConfirmLeaveCurrentPageAsync()) HideLauncher();
    }

    private void BuildNavigation()
    {
        _panelNavBusy = true;
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
        _panelNavBusy = false;
    }

    private void SelectPanelPage(string page)
    {
        if (!_panelMode) return;
        if (_panelPage == page) { RunAfterPageShown(); return; }
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
        if (_panelNavBusy || !_panelMode) return;
        var previous = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
        if (PanelNav.SelectedItem is not ListViewItem { Tag: NavEntry { Page: { } page } } || page == _panelPage)
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
        _panelNavBusy = true;
        PanelNav.SelectedItem = previous;
        _panelNavBusy = false;
    }

    private void ShowPanelPage(string page)
    {
        _panelPage = page;
        switch (page)
        {
            case SnippetsPage:
                _snippetsView ??= new SnippetsView(_services, _hwnd);
                PanelContent.Content = _snippetsView;
                break;
            case ActionsPage:
                if (_actionsView is null)
                {
                    _actionsView = new ActionsView(_services, _hwnd)
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
                }
                PanelContent.Content = _actionsView;
                break;
            default:
                PanelContent.Content = _settingsView;
                _settingsView?.ShowPage(page);
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

    private async void OnPanelBackClick(object sender, RoutedEventArgs e) => await TryLeavePanelAsync();

    private async void OnPanelPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        if (_settingsView?.IsCapturing == true || _actionsView?.IsCapturing == true) return;   // Esc cancels a hotkey recording first
        e.Handled = true;
        await TryLeavePanelAsync();
    }

    private void PositionPanel()
    {
        var (cx, cy) = WindowHelpers.CursorPosition();
        var area = DisplayArea.GetFromPoint(new PointInt32(cx, cy), DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Move(new PointInt32(area.X + 40, area.Y + 40));   // so the window takes that monitor's DPI before it is sized
        double scale = WindowHelpers.GetScale(_hwnd);
        int width = Math.Min((int)(PanelWidthDip * scale), area.Width - 40);
        int height = Math.Min((int)(PanelHeightDip * scale), area.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }
}
