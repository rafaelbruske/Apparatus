using System.Collections.ObjectModel;
using Apparatus.Core;
using Apparatus.Core.Indexing;
using Apparatus.Native;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace Apparatus.App;

public sealed partial class LauncherWindow : Window
{
    private const double WidthDip = 680;
    private const double SearchRowDip = 64;
    private const double ResultRowDip = 56;
    private const double ListPaddingDip = 12;
    private const double FooterDip = 36;
    private const double TopOffsetRatio = 0.16;

    private readonly AppServices _services;
    private readonly ResultExecutor _executor;
    private readonly IconCache _icons;
    private readonly nint _hwnd;
    private CancellationTokenSource? _searchCts;
    private DateTime _shownAtUtc;
    private bool _allowClose;
    private bool _prewarming;

    public ObservableCollection<ResultItemViewModel> Items { get; } = new();

    public LauncherWindow(AppServices services, ResultExecutor executor)
    {
        _services = services;
        _executor = executor;
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _icons = new IconCache(DispatcherQueue);

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

        Activated += OnActivated;
        AppWindow.Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            HideLauncher();
        };
        services.Index.StatusChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateIndexStatus);
        HookAssistant();
    }

    public bool IsLauncherVisible { get; private set; }

    public void AllowClose() => _allowClose = true;

    // ---------------------------------------------------------- show/hide

    /// <summary>
    /// Shows the window once, off-screen and without activation, so the first real show is instant
    /// (creating the XAML tree and the swap chain is what costs ~250 ms the first time).
    /// </summary>
    public void Prewarm(Action? done = null)
    {
        _prewarming = true;
        AppWindow.MoveAndResize(new RectInt32(-12000, -12000, 680, 120));
        AppWindow.Show(activateWindow: false);

        void OnRendered(object? s, object e)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendered -= OnRendered;
            AppWindow.Hide();
            _prewarming = false;
            done?.Invoke();
        }
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendered += OnRendered;
    }

    public void ToggleLauncher()
    {
        if (_macroHidden) return;   // a macro is being recorded: the window is hidden on purpose
        if (IsLauncherVisible && _panelMode && _isActive)
        {
            _ = ConfirmThenHideAsync();   // closing the panel with the hotkey asks about unsaved edits too
            return;
        }
        // A kept-open AI window that lost focus: the hotkey brings it back to the text box instead of hiding it.
        if (IsLauncherVisible && !_isActive && (_panelMode || (_aiMode && _services.Settings.Current.AssistantKeepOpen)))
        {
            Activate();
            WindowHelpers.BringToForeground(_hwnd);
            DispatcherQueue.TryEnqueue(() => (_panelMode ? (Control)PanelNav : SearchBox).Focus(FocusState.Programmatic));
            return;
        }
        if (IsLauncherVisible) HideLauncher();
        else ShowLauncher();
    }

    public void ShowLauncher(string? initialQuery = null, string? panelPage = null)
    {
        if (panelPage is not null && _panelMode && IsLauncherVisible)
        {
            OpenSettingsPanel(panelPage);
            Activate();
            WindowHelpers.BringToForeground(_hwnd);
            return;
        }
        LeavePanel(search: false);   // a fresh show always starts from the search
        if (_prewarming) AppWindow.Hide();
        _ = _services.RefreshAppsAsync();

        // Remember who had the focus: a snippet picked here is inserted there.
        _services.Expander?.RememberLauncherTarget(Native.KeyTranslator.ForegroundWindow());
        IsLauncherVisible = true;
        _shownAtUtc = DateTime.UtcNow;
        _services.OnLauncherShownForAssistant();   // forgets the conversation when it expired or the user wants a fresh one each time
        LeaveAiVisuals();
        SearchBox.Text = initialQuery ?? "";   // TextChanged runs the search when the text is not empty
        PositionOnActiveMonitor();

        AppWindow.Show();
        Activate();
        WindowHelpers.BringToForeground(_hwnd);
        if (panelPage is not null)
        {
            EnterPanel(panelPage);
            return;
        }
        DispatcherQueue.TryEnqueue(() =>
        {
            SearchBox.Focus(FocusState.Programmatic);
            if (string.IsNullOrEmpty(initialQuery)) SearchBox.SelectAll();
            else SearchBox.Select(SearchBox.Text.Length, 0);
        });

        if (string.IsNullOrEmpty(initialQuery)) _ = RunSearchAsync("");   // empty query: recent and frequent items
        UpdateIndexStatus();
    }

    public void HideLauncher()
    {
        if (!IsLauncherVisible) return;
        IsLauncherVisible = false;
        _searchCts?.Cancel();
        LeavePanel(search: false);
        AppWindow.Hide();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // Clicking elsewhere dismisses the launcher, like Spotlight/Raycast. The grace period avoids
        // closing it during the focus churn right after it opens.
        _isActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (args.WindowActivationState == WindowActivationState.Deactivated && IsLauncherVisible &&
            (DateTime.UtcNow - _shownAtUtc).TotalMilliseconds > 300 &&
            !_panelMode && !(_aiMode && _services.Settings.Current.AssistantKeepOpen))
        {
            HideLauncher();
        }
    }

    private void PositionOnActiveMonitor()
    {
        var (cx, cy) = WindowHelpers.CursorPosition();
        var area = DisplayArea.GetFromPoint(new PointInt32(cx, cy), DisplayAreaFallback.Nearest).WorkArea;

        // Move first so the window picks up that monitor's DPI, then size it.
        AppWindow.Move(new PointInt32(area.X + 40, area.Y + 40));
        double scale = WindowHelpers.GetScale(_hwnd);
        int width = (int)(WidthDip * scale);
        int height = (int)(SearchRowDip * scale);
        int x = area.X + (area.Width - width) / 2;
        int y = area.Y + (int)(area.Height * TopOffsetRatio);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        ResizeToContent();
    }

    private void ResizeToContent()
    {
        if (_panelMode) return;
        double scale = WindowHelpers.GetScale(_hwnd);
        double height = SearchRowDip;
        if (_aiMode) height += 1 + ChatDip + FooterDip;
        else if (Items.Count > 0) height += 1 + Items.Count * ResultRowDip + ListPaddingDip + FooterDip;
        AppWindow.ResizeClient(new SizeInt32((int)(WidthDip * scale), (int)(height * scale)));
    }

    // -------------------------------------------------------------- search

    /// <summary>
    /// A long text (typed, pasted or put back after a failed question) must show its end, where the caret is, instead of its start.
    /// The text box does not always scroll by itself after a paste or a programmatic change, so its inner scroller is moved here.
    /// </summary>
    private void KeepCaretVisible()
    {
        if (SearchBox.Text.Length == 0 || SearchBox.SelectionStart != SearchBox.Text.Length) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var scroller = FindDescendant<ScrollViewer>(SearchBox);
            if (scroller is null) return;
            scroller.UpdateLayout();
            scroller.ChangeView(scroller.ScrollableWidth, null, null, disableAnimation: true);
        });
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private async void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLauncherVisible && !_prewarming) return;
        KeepCaretVisible();
        if (_aiMode)   // in AI mode the box holds a question, not a search
        {
            UpdateAiFooter();   // Enter means "run the button" only while the box is empty
            return;
        }
        await RunSearchAsync(SearchBox.Text);
    }

    private async Task RunSearchAsync(string text, int keepSelection = -1)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            // Tiny debounce: keystrokes that arrive back-to-back cancel each other before doing any work.
            if (text.Length > 0) await Task.Delay(15, cts.Token);
            var results = await _services.Search.SearchAsync(text, _services.Settings.Current.MaxResults, cts.Token);
            if (cts.IsCancellationRequested) return;
            ShowResults(results);
            if (keepSelection >= 0 && Items.Count > 0) ResultsList.SelectedIndex = Math.Min(keepSelection, Items.Count - 1);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
        catch (Exception ex)
        {
            AppLog.Error("Search failed", ex);
        }
    }

    private void ShowResults(IReadOnlyList<Core.Search.SearchResult> results)
    {
        if (_aiMode || _panelMode) return;   // a late search must not bring the list back over the conversation
        Items.Clear();
        foreach (var r in results)
        {
            var vm = new ResultItemViewModel(r);
            Items.Add(vm);
            if (r.Icon is not null) _ = LoadIconAsync(vm, r.Icon);
        }

        bool any = Items.Count > 0;
        var visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Divider.Visibility = visibility;
        ResultsList.Visibility = visibility;
        Footer.Visibility = visibility;
        ResultsList.SelectedIndex = any ? 0 : -1;
        if (any) ResultsList.ScrollIntoView(Items[0]);
        ResizeToContent();
        UpdateHints();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateHints();

    /// <summary>The footer explains what Enter and Ctrl+Enter will do for the selected result.</summary>
    private void UpdateHints()
    {
        var selected = (ResultsList.SelectedItem as ResultItemViewModel)?.Result;
        string star = selected is null ? "" : selected.Kind == Core.Search.ResultKind.Clipboard ? "     Ctrl+D Fixar"
            : Core.Storage.UsageStore.CanFavorite(selected) ? (selected.Favorite ? "     Ctrl+D Desafixar" : "     Ctrl+D Favoritar") : "";
        if (selected is not null && Core.Start.StartLayoutStore.CanPin(selected))
            star += _services.StartLayout.IsPinned(selected.Key) ? "     Ctrl+P Tirar do Iniciar" : "     Ctrl+P Fixar no Iniciar";
        HintsText.Text = selected?.Kind switch
        {
            Core.Search.ResultKind.File or Core.Search.ResultKind.Folder => "↵ Abrir     Ctrl+↵ Mostrar na pasta     Esc Fechar",
            Core.Search.ResultKind.Calculator => "↵ Copiar o resultado     Esc Fechar",
            Core.Search.ResultKind.Snippet => "↵ Inserir     Ctrl+↵ Copiar     Esc Fechar",
            Core.Search.ResultKind.Action => "↵ Executar     Esc Fechar",
            Core.Search.ResultKind.Clipboard => "↵ Colar     Ctrl+↵ Copiar     Shift+Del Esquecer     Esc Fechar",
            Core.Search.ResultKind.Extension => "↵ Executar     Ctrl+↵ Alternativa     Esc Fechar",
            Core.Search.ResultKind.Command => "↵ Executar     Esc Fechar",
            _ => "↵ Abrir     Esc Fechar",
        } + star + (CanEnterAiMode() ? "     Tab Perguntar à IA" : "");
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

    private void UpdateIndexStatus()
    {
        var status = _services.Index.Status;
        bool scanning = status.Any(s => s.State is RootState.Scanning or RootState.Pending);
        StatusText.Text = scanning ? $"Indexando arquivos… ({_services.Index.TotalEntries:N0} itens)" : "";
    }

    // ----------------------------------------------------------- keyboard

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnSearchPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsDown(VirtualKey.Control), shift = IsDown(VirtualKey.Shift);
        if (_aiMode)
        {
            OnAiKeyDown(e, ctrl, shift);
            return;
        }
        switch (e.Key)
        {
            case (VirtualKey)188 when ctrl:   // Ctrl+,
                OpenSettingsPanel("general");
                e.Handled = true;
                break;
            case VirtualKey.Down:
                MoveSelection(+1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case VirtualKey.Tab:
                if (!shift && CanEnterAiMode()) EnterAiMode();
                else MoveSelection(shift ? -1 : +1);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                ExecuteSelected(secondary: ctrl);
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                if (SearchBox.Text.Length > 0) SearchBox.Text = "";
                else HideLauncher();
                e.Handled = true;
                break;
            case VirtualKey.D when ctrl:
                ToggleFavoriteOfSelected();
                e.Handled = true;
                break;
            case VirtualKey.P when ctrl:
                TogglePinToStart();
                e.Handled = true;
                break;
            case VirtualKey.Delete when shift && (ResultsList.SelectedItem as ResultItemViewModel)?.Result.Kind == Core.Search.ResultKind.Clipboard:
                ForgetClipboardEntry();
                e.Handled = true;
                break;
            case VirtualKey.Delete when shift && string.IsNullOrEmpty(SearchBox.Text):
                ForgetSelected();
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (Items.Count == 0) return;
        int next = (ResultsList.SelectedIndex + delta + Items.Count) % Items.Count;
        ResultsList.SelectedIndex = next;
        ResultsList.ScrollIntoView(Items[next]);
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ResultItemViewModel vm) Execute(vm, secondary: false);
    }

    private void ExecuteSelected(bool secondary)
    {
        if (ResultsList.SelectedItem is ResultItemViewModel vm) Execute(vm, secondary);
    }

    private void Execute(ResultItemViewModel vm, bool secondary)
    {
        string query = SearchBox.Text;
        HideLauncher();   // hide first so the launched program gets the foreground
        _executor.Run(vm.Result, query, secondary);
    }

    private void ToggleFavoriteOfSelected()
    {
        if (ResultsList.SelectedItem is not ResultItemViewModel vm) return;
        int index = ResultsList.SelectedIndex;
        if (vm.Result.Kind == Core.Search.ResultKind.Clipboard)
        {
            // Clipboard entries are pinned inside the history (so they survive its clean-ups), not in the favorites list.
            _services.ClipboardHost?.TogglePin(long.Parse(vm.Result.Payload));
            _ = RunSearchAsync(SearchBox.Text, keepSelection: index);
            return;
        }
        if (!Core.Storage.UsageStore.CanFavorite(vm.Result)) return;
        _services.Usage.ToggleFavorite(vm.Result);
        _ = RunSearchAsync(SearchBox.Text, keepSelection: index);
    }

    /// <summary>Ctrl+P: pins the selected result to the Start menu, or unpins it when it already is.</summary>
    private void TogglePinToStart()
    {
        if (ResultsList.SelectedItem is not ResultItemViewModel vm || !Core.Start.StartLayoutStore.CanPin(vm.Result)) return;
        var store = _services.StartLayout;
        bool wasPinned = store.IsPinned(vm.Result.Key);
        if (wasPinned) store.Unpin(vm.Result.Key);
        else store.Pin(vm.Result);
        StatusText.Text = wasPinned ? $"“{vm.Result.Title}” saiu do Iniciar" : $"“{vm.Result.Title}” fixado no Iniciar";
        UpdateHints();
    }

    private void ForgetClipboardEntry()
    {
        if (ResultsList.SelectedItem is not ResultItemViewModel vm) return;
        int index = ResultsList.SelectedIndex;
        _services.ClipboardStore.Delete(long.Parse(vm.Result.Payload));
        _ = RunSearchAsync(SearchBox.Text, keepSelection: index);
    }

    private void ForgetSelected()
    {
        if (ResultsList.SelectedItem is not ResultItemViewModel vm) return;
        _services.Usage.Forget(vm.Result.Key);
        _ = RunSearchAsync("");
    }
}
