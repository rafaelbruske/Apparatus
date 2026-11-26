using System.Collections.ObjectModel;
using Apparatus.Core.Indexing;
using Apparatus.Core.Search.Providers;
using Apparatus.Core.Settings;
using Apparatus.Core.Input;
using Apparatus.Native;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Apparatus.App;

public sealed partial class SettingsView : UserControl
{
    private readonly AppServices _services;
    private readonly nint _hwnd;
    private readonly ObservableCollection<string> _roots = new();
    private bool _loading = true;
    private bool _capturing;
    private long _menuGuardUntil;   // the system-menu request follows the key by a few milliseconds: keep dropping it briefly after recording
    private readonly KeyMenuSuppressor _keyMenu;

    public SettingsView(AppServices services, nint hwnd)
    {
        _services = services;
        _hwnd = hwnd;
        InitializeComponent();

        // Recording "Alt+Space" must not also open the host window's system menu (Windows does that after the app has seen the key).
        _keyMenu = new KeyMenuSuppressor(_hwnd, () => _capturing || _capturingShot || Environment.TickCount64 < _menuGuardUntil);

        RootsList.ItemsSource = _roots;
        DataPathText.Text = $"{AppPaths.DataDirectory}\nConfiguração: {AppPaths.SettingsFile}";
        LoadFromSettings();

        _services.Index.StatusChanged += OnIndexStatusChanged;
        Closed += (_, _) =>
        {
            _services.Index.StatusChanged -= OnIndexStatusChanged;
            if (_capturingShot) StopCapturingShot();
            _keyMenu.Dispose();
            if (_capturing)
            {
                _services.ResumeHotkey();
                _services.ActionHost?.ResumeHotkeys();
            }
        };
        _loading = false;
        ShowPage(PageIds[0]);
    }

    /// <summary>Raised by <see cref="Close"/>: the host is done with this view, so everything it subscribed to must be released.</summary>
    public event EventHandler? Closed;

    public void Close() => Closed?.Invoke(this, EventArgs.Empty);

    /// <summary>True while the user is recording a new launcher hotkey: the host must leave the keys alone (Esc cancels the recording).</summary>
    public bool IsCapturing => _capturing || _capturingShot;

    /// <summary>The pages of the settings, in the order of the side bar. Each is a group of cards of the long page this used to be.</summary>
    public static readonly IReadOnlyList<string> PageIds = new[]
    {
        "general", "search", "backup", "about", "aboutapp", "snippets", "clipboard", "capture", "extensions", "start", "assistant", "windows",
    };

    private static readonly Dictionary<string, string> PageTitles = new()
    {
        ["general"] = "Geral", ["search"] = "Pesquisa", ["backup"] = "Backup e sincronização", ["about"] = "Atualizações e dados",
        ["aboutapp"] = "Sobre", ["snippets"] = "Snippets", ["clipboard"] = "Área de transferência", ["capture"] = "Captura de tela", ["extensions"] = "Extensões", ["start"] = "Menu Iniciar do Apparatus",
        ["assistant"] = "Assistente de IA", ["windows"] = "Windows",
    };

    // -------------------------------------------------------------- search

    /// <summary>A part of a page that stays or goes as a whole: a heading and the card under it.</summary>
    private sealed class SearchBlock
    {
        public SearchBlock(string pageId) => PageId = pageId;

        public string PageId { get; }

        public List<UIElement> Elements { get; } = new();

        public System.Text.StringBuilder Text { get; } = new();
    }

    private readonly Dictionary<UIElement, Visibility> _visibilityBeforeFilter = new();

    /// <summary>A line under the page title that explains an empty search result; null hides it.</summary>
    public void ShowSearchNote(string? text)
    {
        SearchEmptyText.Text = text ?? "";
        SearchEmptyText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Shows only the blocks whose text has every word of <paramref name="query"/> (the title of the page counts as part of the
    /// text, so searching for a page name keeps the whole page). A blank query puts everything back. Returns, for each page, how
    /// many blocks match; pages that are not in the result have none.
    /// </summary>
    public IReadOnlyDictionary<string, int> ApplyFilter(string? query)
    {
        var words = SettingsSearch.Words(query);
        var counts = new Dictionary<string, int>();
        ClearHighlights();

        foreach (var block in BuildSearchBlocks())
        {
            bool visible = words.Count == 0 || SettingsSearch.Matches(PageTitles[block.PageId] + " " + block.Text, words);
            foreach (var element in block.Elements)
            {
                if (!_visibilityBeforeFilter.ContainsKey(element)) _visibilityBeforeFilter[element] = element.Visibility;
                element.Visibility = visible ? _visibilityBeforeFilter[element] : Visibility.Collapsed;
            }
            if (!visible || words.Count == 0) continue;
            counts[block.PageId] = counts.GetValueOrDefault(block.PageId) + 1;
            foreach (var element in block.Elements) Highlight(element, words);
        }

        if (words.Count == 0) _visibilityBeforeFilter.Clear();
        return counts;
    }

    /// <summary>
    /// The blocks of every page, as written in the XAML: a heading starts a block, and the elements that follow it (the card) belong to
    /// it. A new setting is found without being listed anywhere.
    /// </summary>
    private IEnumerable<SearchBlock> BuildSearchBlocks()
    {
        foreach (var id in PageIds)
        {
            if (FindName("Page_" + id) is not Panel page) continue;

            SearchBlock? current = null;
            bool currentHasBody = false;
            foreach (UIElement child in page.Children)
            {
                bool heading = child is TextBlock;
                // A heading starts a block unless the block so far is only headings.
                if (current is null || (heading && currentHasBody))
                {
                    if (current is not null) yield return current;
                    current = new SearchBlock(id);
                    currentHasBody = false;
                }
                if (!heading) currentHasBody = true;
                current.Elements.Add(child);
                CollectSearchText(child, current.Text);
            }
            if (current is not null) yield return current;
        }
    }

    private static void CollectSearchText(UIElement element, System.Text.StringBuilder into)
    {
        void Add(object? text)
        {
            if (text is TextBlock inner) text = inner.Text;
            if (text is string s && s.Trim().Length > 0) into.Append(s.Trim()).Append(' ');
        }

        switch (element)
        {
            case TextBlock block: Add(block.Text); break;
            case CheckBox check: Add(check.Content); break;
            case Button button when button.Content is string: Add(button.Content); break;
            case InfoBar bar: Add(bar.Title); Add(bar.Message); break;
            default: Add(HeaderOf(element)); break;
        }
        foreach (var child in ChildrenOf(element)) CollectSearchText(child, into);
    }

    /// <summary>The elements inside a panel, a card or a control that holds one.</summary>
    private static IEnumerable<UIElement> ChildrenOf(UIElement element) => element switch
    {
        Panel panel => panel.Children,
        Border { Child: { } child } => new[] { child },
        ContentControl { Content: UIElement content } => new[] { content },
        _ => Array.Empty<UIElement>(),
    };

    private static object? HeaderOf(UIElement element) => element switch
    {
        ToggleSwitch t => t.Header,
        ComboBox c => c.Header,
        TextBox b => b.Header,
        PasswordBox p => p.Header,
        NumberBox n => n.Header,
        RadioButtons r => r.Header,
        _ => null,
    };

    private static void SetHeader(UIElement element, object? header)
    {
        switch (element)
        {
            case ToggleSwitch t: t.Header = header; break;
            case ComboBox c: c.Header = header; break;
            case TextBox b: b.Header = header; break;
            case PasswordBox p: p.Header = header; break;
            case NumberBox n: n.Header = header; break;
            case RadioButtons r: r.Header = header; break;
        }
    }

    // ------------------------------------------------- marking what was found

    private readonly List<TextBlock> _marked = new();
    private readonly Dictionary<UIElement, object?> _plainHeaders = new();

    /// <summary>Marks the words searched for in the texts and in the headers of the controls, so the setting is easy to spot.</summary>
    private void Highlight(UIElement element, IReadOnlyList<string> words)
    {
        if (element is TextBlock block)
        {
            Mark(block, words);
        }
        else if (HeaderOf(element) is string header && SettingsSearch.FindRanges(header, words).Count > 0)
        {
            // A header given as plain text cannot be marked: show it through a text block of its own, and put the text back afterwards.
            var text = new TextBlock { Text = header, TextWrapping = TextWrapping.Wrap };
            Mark(text, words);
            _plainHeaders[element] = header;
            SetHeader(element, text);
        }
        foreach (var child in ChildrenOf(element)) Highlight(child, words);
    }

    private void Mark(TextBlock block, IReadOnlyList<string> words)
    {
        var ranges = SettingsSearch.FindRanges(block.Text, words);
        if (ranges.Count == 0) return;

        // A warm highlighter with dark text: readable on the dark and on the light theme alike.
        var highlighter = new Microsoft.UI.Xaml.Documents.TextHighlighter
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(235, 255, 200, 40)),
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 20, 20, 20)),
        };
        foreach (var (start, length) in ranges) highlighter.Ranges.Add(new Microsoft.UI.Xaml.Documents.TextRange { StartIndex = start, Length = length });
        block.TextHighlighters.Add(highlighter);
        _marked.Add(block);
    }

    private void ClearHighlights()
    {
        foreach (var block in _marked) block.TextHighlighters.Clear();
        _marked.Clear();
        foreach (var (element, header) in _plainHeaders) SetHeader(element, header);
        _plainHeaders.Clear();
    }

    /// <summary>Shows one page and hides the others.</summary>
    public void ShowPage(string id)
    {
        if (!PageIds.Contains(id)) id = PageIds[0];
        foreach (var page in PageIds)
            if (FindName("Page_" + page) is UIElement element)
                element.Visibility = page == id ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = PageTitles[id];
        Root.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void LoadFromSettings()
    {
        var s = _services.Settings.Current;
        HotkeyText.Text = s.Hotkey;
        AutostartSwitch.IsOn = s.RunAtStartup;
        MaxResultsBox.Value = s.MaxResults;
        var searches = s.SearchEngines.Where(Quicklinks.IsSearch).ToList();
        EngineBox.ItemsSource = searches;
        EngineBox.SelectedItem = searches.FirstOrDefault(e => e.Keyword == s.DefaultSearchEngine) ?? searches.FirstOrDefault();
        ExpansionSwitch.IsOn = s.SnippetExpansion;
        ExcludedProcessesBox.Text = string.Join("\r", s.SnippetExcludedProcesses);
        IndexSwitch.IsOn = s.IndexFiles;
        RootsMode.SelectedIndex = s.IndexRoots is null ? 0 : 1;
        _roots.Clear();
        foreach (var r in s.IndexRoots ?? new()) _roots.Add(r);
        ExcludedBox.Text = string.Join("\r", s.ExcludedNames);
        UpdateRootsVisibility();
        UpdateIndexStatus();
        LoadPhase4();
        LoadAssistant();
        LoadStart();
        LoadBackup();
        LoadUpdates();
        LoadAbout();
        LoadCapture();
    }

    // ------------------------------------------------------------ hotkey

    private void OnChangeHotkey(object sender, RoutedEventArgs e)
    {
        if (_capturing) { StopCapturing(resume: true); return; }
        _capturing = true;
        _services.SuspendHotkey();   // so pressing the current combination does not open the launcher
        _services.ActionHost?.SuspendHotkeys();   // nor run an action that uses the same keys
        ChangeHotkeyButton.Content = "Cancelar";
        HotkeyText.Text = "Pressione…";
        HotkeyInfo.IsOpen = false;
        HotkeyHelp.Text = "Pressione a nova combinação (por exemplo Alt+Espaço). Esc cancela.";
        ChangeHotkeyButton.Focus(FocusState.Programmatic);
    }

    private void OnResetHotkey(object sender, RoutedEventArgs e)
    {
        if (_capturing) StopCapturing(resume: true);
        TryApplyHotkey(HotkeySpec.Default);
    }

    private void StopCapturing(bool resume)
    {
        _menuGuardUntil = Environment.TickCount64 + 800;
        _capturing = false;
        ChangeHotkeyButton.Content = "Alterar";
        HotkeyHelp.Text = "Clique em Alterar e pressione a nova combinação.";
        HotkeyText.Text = _services.Settings.Current.Hotkey;
        if (resume) _services.ResumeHotkey();
        _services.ActionHost?.ResumeHotkeys();
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_capturingShot) { OnCaptureHotkeyKey(e); return; }
        if (!_capturing) return;
        e.Handled = true;

        uint vk = (uint)e.Key;
        if (HotkeySpec.IsModifierKey(vk)) return;   // wait for the "real" key

        var mods = HotkeyModifiers.None;
        if (IsDown(VirtualKey.Control)) mods |= HotkeyModifiers.Control;
        if (IsDown(VirtualKey.Menu)) mods |= HotkeyModifiers.Alt;
        if (IsDown(VirtualKey.Shift)) mods |= HotkeyModifiers.Shift;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) mods |= HotkeyModifiers.Win;

        if (e.Key == VirtualKey.Escape && mods == HotkeyModifiers.None)
        {
            StopCapturing(resume: true);
            return;
        }

        var spec = new HotkeySpec(mods, vk);
        if (!spec.IsValid)
        {
            ShowHotkeyMessage("Use pelo menos um modificador (Ctrl, Alt, Shift ou Win) junto com a tecla.", InfoBarSeverity.Warning);
            return;
        }

        StopCapturing(resume: false);
        TryApplyHotkey(spec);
    }

    private void TryApplyHotkey(HotkeySpec spec)
    {
        // Windows would report these keys as "taken by another app" because the action registered them in this very process.
        if (_services.FindActionUsing(spec) is { } actionName)
        {
            _services.ResumeHotkey();
            HotkeyText.Text = _services.Settings.Current.Hotkey;
            ShowHotkeyMessage($"{spec} já é o atalho da ação “{actionName}”. Escolha outra combinação ou mude o atalho dessa ação.", InfoBarSeverity.Error);
            return;
        }

        var result = _services.TryChangeHotkey(spec);
        HotkeyText.Text = _services.Settings.Current.Hotkey;
        if (result.Success)
        {
            ShowHotkeyMessage($"Atalho alterado para {spec}.", InfoBarSeverity.Success);
            return;
        }

        string reason = result.Win32Error == 1409
            ? $"{spec} já está em uso por outro aplicativo (o Raycast, por exemplo). Feche-o ou escolha outra combinação."
            : $"Não foi possível registrar {spec} (erro {result.Win32Error}).";
        if (_services.ActiveHotkey is { } active) reason += $" Continua valendo {active}.";
        else reason += " Nenhum atalho está ativo no momento.";
        ShowHotkeyMessage(reason, InfoBarSeverity.Error);
    }

    private void ShowHotkeyMessage(string text, InfoBarSeverity severity)
    {
        HotkeyInfo.Severity = severity;
        HotkeyInfo.Message = text;
        HotkeyInfo.IsOpen = true;
    }

    // ----------------------------------------------------------- general

    private void OnAutostartToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.ApplySettings(s => s.RunAtStartup = AutostartSwitch.IsOn);
    }

    private void OnMaxResultsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        _services.ApplySettings(s => s.MaxResults = (int)Math.Clamp(args.NewValue, 3, 12));
    }

    private void OnEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EngineBox.SelectedItem is not Core.Settings.WebSearchEngine engine) return;
        _services.ApplySettings(s => s.DefaultSearchEngine = engine.Keyword);
    }

    // ---------------------------------------------------------- snippets

    private void OnExpansionToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.ApplySettings(s => s.SnippetExpansion = ExpansionSwitch.IsOn);
    }

    private void OnExcludedProcessesLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var names = ExcludedProcessesBox.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.SequenceEqual(_services.Settings.Current.SnippetExcludedProcesses, StringComparer.OrdinalIgnoreCase)) return;
        _services.ApplySettings(s => s.SnippetExcludedProcesses = names);
    }

    private void OnOpenSnippets(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenSnippets();

    // ------------------------------------------------------------- files

    private void OnIndexToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.ApplySettings(s => s.IndexFiles = IndexSwitch.IsOn);
    }

    private void OnRootsModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        bool custom = RootsMode.SelectedIndex == 1;
        if (custom && _roots.Count == 0)
        {
            // Start from what "automatic" would have indexed, so the user can prune it.
            foreach (var r in FileIndex.ResolveRoots(new Core.Settings.AppSettings())) _roots.Add(r);
        }
        UpdateRootsVisibility();
        SaveRoots();
    }

    private void UpdateRootsVisibility() =>
        CustomRootsPanel.Visibility = RootsMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;

    private async void OnAddRoot(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        if (!_roots.Contains(folder.Path, StringComparer.OrdinalIgnoreCase)) _roots.Add(folder.Path);
        SaveRoots();
    }

    private void OnRemoveRoot(object sender, RoutedEventArgs e)
    {
        if (RootsList.SelectedItem is string selected) _roots.Remove(selected);
        SaveRoots();
    }

    private void SaveRoots()
    {
        if (_loading) return;
        var custom = RootsMode.SelectedIndex == 1;
        _services.ApplySettings(s => s.IndexRoots = custom ? _roots.ToList() : null);
    }

    private void OnExcludedLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var names = ExcludedBox.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.SequenceEqual(_services.Settings.Current.ExcludedNames, StringComparer.OrdinalIgnoreCase)) return;
        _services.ApplySettings(s => s.ExcludedNames = names);
    }

    private void OnReindex(object sender, RoutedEventArgs e) => _services.ReindexNow();

    private void OnIndexStatusChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateIndexStatus);

    private void UpdateIndexStatus()
    {
        var status = _services.Index.Status;
        if (!_services.Settings.Current.IndexFiles) { IndexStatusText.Text = "A pesquisa de arquivos está desativada."; return; }
        if (status.Count == 0) { IndexStatusText.Text = "Nenhuma pasta indexada ainda."; return; }

        IndexStatusText.Text = string.Join("\n", status.Select(s => s.State switch
        {
            RootState.Scanning => $"{s.Root}  —  indexando…",
            RootState.Pending => $"{s.Root}  —  aguardando",
            RootState.Failed => $"{s.Root}  —  falhou (veja o log)",
            _ => $"{s.Root}  —  {s.Entries:N0} itens" +
                 (s.Truncated ? " (limite atingido)" : "") +
                 (s.ScannedAtUtc is { } at ? $", atualizado {at.ToLocalTime():dd/MM HH:mm}" : ""),
        }));
    }


    // --------------------------------------------------------- clipboard

    private void OnClipboardToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.ApplySettings(s => s.ClipboardHistory = ClipboardSwitch.IsOn);
    }

    private void OnClipboardLimitsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(ClipboardMaxBox.Value) || double.IsNaN(ClipboardDaysBox.Value)) return;
        _services.ApplySettings(s =>
        {
            s.ClipboardMaxEntries = (int)ClipboardMaxBox.Value;
            s.ClipboardRetentionDays = (int)ClipboardDaysBox.Value;
        });
    }

    private void OnClipboardExcludedLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var names = ClipboardExcludedBox.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.SequenceEqual(_services.Settings.Current.ClipboardExcludedProcesses, StringComparer.OrdinalIgnoreCase)) return;
        _services.ApplySettings(s => s.ClipboardExcludedProcesses = names);
    }

    private void OnClearClipboard(object sender, RoutedEventArgs e) => _services.ClipboardStore.Clear();

    // ------------------------------------------------------- quicklinks

    private void RefreshQuicklinks()
    {
        QuicklinksList.ItemsSource = _services.Settings.Current.SearchEngines
            .Select(q => $"{q.Name}   ·   {q.Keyword}   →   {q.UrlTemplate}")
            .ToList();
    }

    private async void OnAddQuicklink(object sender, RoutedEventArgs e)
    {
        var added = await AskQuicklinkAsync(null);
        if (added is null) return;
        _services.ApplySettings(s => s.SearchEngines.Add(added));
        RefreshQuicklinks();
    }

    private async void OnEditQuicklink(object sender, RoutedEventArgs e)
    {
        int index = QuicklinksList.SelectedIndex;
        var all = _services.Settings.Current.SearchEngines;
        if (index < 0 || index >= all.Count) return;

        var edited = await AskQuicklinkAsync(all[index]);
        if (edited is null) return;
        _services.ApplySettings(s => s.SearchEngines[index] = edited);
        RefreshQuicklinks();
        QuicklinksList.SelectedIndex = index;
    }

    private void OnRemoveQuicklink(object sender, RoutedEventArgs e)
    {
        int index = QuicklinksList.SelectedIndex;
        if (index < 0 || index >= _services.Settings.Current.SearchEngines.Count) return;
        _services.ApplySettings(s => s.SearchEngines.RemoveAt(index));
        RefreshQuicklinks();
    }

    private void OnResetQuicklinks(object sender, RoutedEventArgs e)
    {
        _services.ApplySettings(s =>
        {
            s.SearchEngines = Core.Settings.AppSettings.DefaultEngines();
            s.DefaultSearchEngine = "g";
        });
        RefreshQuicklinks();
        EngineBox.ItemsSource = _services.Settings.Current.SearchEngines.Where(Quicklinks.IsSearch).ToList();
        EngineBox.SelectedItem = (EngineBox.ItemsSource as IEnumerable<Core.Settings.WebSearchEngine>)?.FirstOrDefault();
    }

    private async Task<Core.Settings.WebSearchEngine?> AskQuicklinkAsync(Core.Settings.WebSearchEngine? existing)
    {
        var name = new TextBox { Header = "Nome", Text = existing?.Name ?? "" };
        var keyword = new TextBox { Header = "Atalho (a palavra que você digita no launcher)", Text = existing?.Keyword ?? "" };
        var target = new TextBox
        {
            Header = "Destino",
            Text = existing?.UrlTemplate ?? "",
            PlaceholderText = @"https://…/search?q={query}   ou   D:\Projetos   ou   https://exemplo.com",
        };
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };
        var panel = new StackPanel { Spacing = 12, MinWidth = 440 };
        panel.Children.Add(name);
        panel.Children.Add(keyword);
        panel.Children.Add(target);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = existing is null ? "Novo quicklink" : "Editar quicklink",
            Content = panel,
            PrimaryButtonText = "Salvar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        var candidate = new Core.Settings.WebSearchEngine();
        dialog.PrimaryButtonClick += (_, args) =>
        {
            candidate = new Core.Settings.WebSearchEngine { Name = name.Text.Trim(), Keyword = keyword.Text.Trim(), UrlTemplate = target.Text.Trim() };
            var others = _services.Settings.Current.SearchEngines.Where(q => existing is null || !ReferenceEquals(q, existing) && q.Keyword != existing.Keyword);
            if (Quicklinks.Validate(candidate, others) is { } problem)
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? candidate : null;
    }

    // ------------------------------------------------------- extensions

    private void RebuildExtensions()
    {
        ExtensionsFolderText.Text = "Pasta das extensões: " + _services.Extensions.Folder;
        ExtensionsList.Children.Clear();
        var infos = _services.Extensions.List();
        if (infos.Count == 0)
        {
            ExtensionsList.Children.Add(new TextBlock
            {
                Text = "Nenhuma extensão instalada. Copie uma pasta com um extension.json para a pasta acima, ou use “Restaurar o exemplo”.",
                TextWrapping = TextWrapping.Wrap, Opacity = 0.7,
            });
        }

        foreach (var info in infos)
        {
            var m = info.Manifest;
            var grid = new Grid { ColumnSpacing = 16 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = m.Version.Length > 0 ? $"{m.Name}  ·  v{m.Version}" : m.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (m.Description.Length > 0) text.Children.Add(new TextBlock { Text = m.Description, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 });
            string line = (m.Keyword.Length > 0 ? $"Gatilho: {m.Keyword}   ·   " : "Sem gatilho   ·   ") + (m.IsPersistent ? "processo contínuo" : "um processo por busca")
                          + (info.Enabled ? info.Status switch
                          {
                              Core.Extensions.ExtensionStatus.Running => "   ·   em execução",
                              Core.Extensions.ExtensionStatus.Failed => "   ·   falhou",
                              _ => "   ·   ativada",
                          } : "");
            text.Children.Add(new TextBlock { Text = line, FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
            if (info.Status == Core.Extensions.ExtensionStatus.Failed && !string.IsNullOrEmpty(info.Error))
            {
                text.Children.Add(new TextBlock
                {
                    Text = info.Error, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                });
            }
            grid.Children.Add(text);

            var controls = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
            var toggle = new ToggleSwitch { IsOn = info.Enabled, OnContent = "Ativada", OffContent = "Desativada" };
            toggle.Toggled += async (_, _) => await OnExtensionToggledAsync(m, toggle);
            controls.Children.Add(toggle);
            if (info.Enabled)
            {
                var all = new CheckBox { Content = "Responder a todas as buscas", IsChecked = info.Global };
                RoutedEventHandler changed = (_, _) => SetExtensionGlobal(m.Id, all.IsChecked == true);
                all.Checked += changed;
                all.Unchecked += changed;
                controls.Children.Add(all);
            }
            Grid.SetColumn(controls, 1);
            grid.Children.Add(controls);
            ExtensionsList.Children.Add(grid);
        }

        var errors = _services.Extensions.LoadErrors;
        ExtensionsErrors.Text = "Ignoradas por erro no extension.json:\n" + string.Join("\n", errors);
        ExtensionsErrors.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task OnExtensionToggledAsync(Core.Extensions.ExtensionManifest m, ToggleSwitch toggle)
    {
        if (_loading) return;
        bool on = toggle.IsOn;
        if (on)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = $"Ativar “{m.Name}”?",
                Content = $"Esta extensão é um programa e será executado com as suas permissões:\n\n{m.Command} {string.Join(' ', m.Args)}\n\nAtive apenas se confiar em quem a escreveu."
                          + (m.Keyword.Length > 0 ? $"\n\nEla responde ao gatilho “{m.Keyword}” no launcher." : ""),
                PrimaryButtonText = "Ativar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                RebuildExtensions();
                return;
            }
        }

        _services.ApplySettings(s =>
        {
            s.EnabledExtensions.RemoveAll(id => id.Equals(m.Id, StringComparison.OrdinalIgnoreCase));
            s.GlobalExtensions.RemoveAll(id => id.Equals(m.Id, StringComparison.OrdinalIgnoreCase) && !on);
            if (on) s.EnabledExtensions.Add(m.Id);
        });
        RebuildExtensions();
    }

    private void SetExtensionGlobal(string id, bool global)
    {
        if (_loading) return;
        _services.ApplySettings(s =>
        {
            s.GlobalExtensions.RemoveAll(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (global) s.GlobalExtensions.Add(id);
        });
    }

    private void OnOpenExtensionsFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_services.Extensions.Folder);
        ShellLauncher.Open(_services.Extensions.Folder);
    }

    private void OnReloadExtensions(object sender, RoutedEventArgs e)
    {
        _services.Extensions.Reload();
        RebuildExtensions();
    }

    private void OnRestoreSample(object sender, RoutedEventArgs e)
    {
        try
        {
            ExtensionRunner.CopySample(_services.Extensions.Folder);
            _services.Extensions.Reload();
        }
        catch (Exception ex)
        {
            ExtensionsErrors.Text = ex.Message;
            ExtensionsErrors.Visibility = Visibility.Visible;
            return;
        }
        RebuildExtensions();
    }

    private void LoadPhase4()
    {
        var s = _services.Settings.Current;
        ClipboardSwitch.IsOn = s.ClipboardHistory;
        ClipboardMaxBox.Value = s.ClipboardMaxEntries;
        ClipboardDaysBox.Value = s.ClipboardRetentionDays;
        ClipboardExcludedBox.Text = string.Join("\r", s.ClipboardExcludedProcesses);
        RefreshQuicklinks();
        RebuildExtensions();
    }

    // -------------------------------------------------------------- data

    private void OnClearHistory(object sender, RoutedEventArgs e) => _services.Usage.ClearHistory();

    private void OnOpenData(object sender, RoutedEventArgs e) => ShellLauncher.Open(AppPaths.DataDirectory);
}
