using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Apparatus.Core;
using Apparatus.Core.Actions;
using Apparatus.Core.Input;
using Apparatus.Core.Search;
using Apparatus.Native;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Apparatus.App;

public sealed class ActionListItem
{
    public ActionListItem(ActionDefinition action, HotkeyStatus status)
    {
        Action = action;
        Name = action.Name;
        Subtitle = action.Hotkey.Length > 0 ? $"{action.Hotkey}  ·  {action.Describe(46)}" : action.Describe(56);
        Opacity = action.Enabled ? 1.0 : 0.5;
        WarningVisibility = status.State == HotkeyState.Failed ? Visibility.Visible : Visibility.Collapsed;
    }

    public ActionDefinition Action { get; }
    public string Name { get; }
    public string Subtitle { get; }
    public double Opacity { get; }
    public Visibility WarningVisibility { get; }
}

public sealed class StepListItem : INotifyPropertyChanged
{
    private int _number;

    public StepListItem(ActionStep step)
    {
        Step = step;
        var d = StepCatalog.Get(step.Kind);
        Title = d.Title;
        Glyph = d.Glyph;
    }

    public ActionStep Step { get; }
    public string Title { get; }
    public string Glyph { get; }
    public string Summary => StepCatalog.Summarize(Step);

    public string Number
    {
        get => _number.ToString();
        set { _number = int.Parse(value); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Number))); }
    }

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class ActionsWindow : Window
{
    private const string AllCategories = "Todas as categorias";

    private readonly AppServices _services;
    private readonly nint _hwnd;
    private ActionDefinition _current = new();
    private bool _loading;
    private bool _dirty;
    private bool _revertingSelection;
    private bool _recording;
    private long _menuGuardUntil;
    private readonly KeyMenuSuppressor _keyMenu;
    private StepListItem? _editingStep;

    public ObservableCollection<ActionListItem> Items { get; } = new();
    public ObservableCollection<StepListItem> Steps { get; } = new();

    public ActionsWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        // Recording a hotkey that uses Alt+Space must not also open this window's system menu.
        _keyMenu = new KeyMenuSuppressor(_hwnd, () => _recording || Environment.TickCount64 < _menuGuardUntil);

        AppWindow.SetIcon(AppPaths.AssetPath("apparatus.ico"));
        double scale = WindowHelpers.GetScale(_hwnd);
        AppWindow.Resize(new SizeInt32((int)(1260 * scale), (int)(940 * scale)));

        BuildMenus();

        _services.Actions.Changed += OnStoreChanged;
        if (_services.ActionHost is { } host) host.StatusChanged += OnStatusChanged;
        Closed += (_, _) =>
        {
            _services.Actions.Changed -= OnStoreChanged;
            if (_services.ActionHost is { } h) h.StatusChanged -= OnStatusChanged;
            if (_recording) StopRecording();
            AbortMacroRecording();
            _keyMenu.Dispose();
        };

        RefreshCategories();
        RefreshList(null);
        if (Items.Count > 0) ActionList.SelectedIndex = 0;
        else BeginNew(null);
    }

    // ------------------------------------------------------------- menus

    private void BuildMenus()
    {
        void AddNew(string text, StepKind? kind)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += async (_, _) => { if (await ConfirmLeaveAsync()) BeginNew(kind); };
            NewFlyout.Items.Add(item);
        }
        AddNew("Abrir um aplicativo", StepKind.OpenApp);
        AddNew("Abrir um site", StepKind.OpenUrl);
        AddNew("Gerenciar a janela ativa", StepKind.Window);
        AddNew("Executar um script do PowerShell", StepKind.RunPowerShell);
        AddNew("Enviar teclas", StepKind.SendKeys);
        NewFlyout.Items.Add(new MenuFlyoutSeparator());
        AddNew("Macro (vários passos)", null);
        var record = new MenuFlyoutItem { Text = "Gravar uma macro do teclado…", Icon = new FontIcon { Glyph = "" } };
        record.Click += async (_, _) => { if (await ConfirmLeaveAsync()) await RecordMacroAsync(append: false); };
        NewFlyout.Items.Add(record);

        foreach (var d in StepCatalog.All)
        {
            var item = new MenuFlyoutItem { Text = d.Title, Icon = new FontIcon { Glyph = d.Glyph } };
            var kind = d.Kind;
            item.Click += (_, _) => AddStep(kind);
            AddStepFlyout.Items.Add(item);
        }
    }

    // -------------------------------------------------------------- list

    private void OnStoreChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        RefreshCategories();
        RefreshList(_current.Id == 0 ? null : _current.Id);
    });

    private void OnStatusChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        RefreshList(_current.Id == 0 ? null : _current.Id);
        UpdateHotkeyHelp();
    });

    private void RefreshCategories()
    {
        string? selected = CategoryFilter.SelectedItem as string;
        var categories = new List<string> { AllCategories };
        categories.AddRange(_services.Actions.Categories);
        CategoryFilter.ItemsSource = categories;
        CategoryFilter.SelectedItem = selected is not null && categories.Contains(selected) ? selected : AllCategories;

        string text = CategoryBox.Text;
        bool wasLoading = _loading;
        _loading = true;
        CategoryBox.ItemsSource = categories.Skip(1).ToList();
        SetCategory(CategoryBox, text);
        _loading = wasLoading;
    }

    private void OnFilterChanged(object sender, object e)
    {
        if (_loading) return;
        RefreshList(_current.Id == 0 ? null : _current.Id);
    }

    private void RefreshList(long? selectId)
    {
        var query = FuzzyQuery.Parse(SearchBox.Text);
        string? category = CategoryFilter.SelectedItem as string;
        IEnumerable<ActionDefinition> source = _services.Actions.All;
        if (category is not null && category != AllCategories)
            source = source.Where(a => a.Category.Equals(category, StringComparison.CurrentCultureIgnoreCase));

        List<ActionDefinition> shown = query.IsEmpty
            ? source.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
            : source.Select(a => (a, score: Math.Max(FuzzyMatcher.Score(a.Name, query), FuzzyMatcher.Score(a.Category, query) - 30)))
                    .Where(x => x.score != FuzzyMatcher.NoMatch)
                    .OrderByDescending(x => x.score).Select(x => x.a).ToList();

        _loading = true;
        Items.Clear();
        foreach (var a in shown) Items.Add(new ActionListItem(a, _services.ActionHost?.StatusOf(a.Id) ?? new HotkeyStatus(HotkeyState.None, "")));
        if (selectId is { } id) ActionList.SelectedItem = Items.FirstOrDefault(i => i.Action.Id == id);
        _loading = false;
    }

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _revertingSelection) return;
        if (ActionList.SelectedItem is not ActionListItem item || item.Action.Id == _current.Id) return;

        if (!await ConfirmLeaveAsync())
        {
            _revertingSelection = true;
            ActionList.SelectedItem = Items.FirstOrDefault(i => i.Action.Id == _current.Id);
            _revertingSelection = false;
            return;
        }
        LoadEditor(item.Action.Clone());
    }

    // ------------------------------------------------------------ editor

    private void LoadEditor(ActionDefinition action)
    {
        _loading = true;
        _current = action;
        NameBox.Text = action.Name;
        SetCategory(CategoryBox, action.Category);
        EnabledSwitch.IsOn = action.Enabled;
        HotkeyText.Text = action.Hotkey.Length > 0 ? action.Hotkey : "(sem atalho)";
        ScopeBox.SelectedIndex = (int)action.Scope;
        ScopeAppsBox.Text = string.Join(", ", action.ScopeApps);
        OnErrorBox.SelectedIndex = (int)action.OnError;

        Steps.Clear();
        foreach (var s in action.Steps) Steps.Add(new StepListItem(s));
        RenumberSteps();
        _editingStep = null;
        StepEditor.Visibility = Visibility.Collapsed;
        Status.IsOpen = false;
        _dirty = false;
        _baseline = Fingerprint();
        _loading = false;

        if (Steps.Count > 0) StepsList.SelectedIndex = 0;
        UpdateDerived();
    }

    // WinUI raises TextChanged and friends later than the assignment that caused them, after _loading is false again, so they used to
    // mark a freshly loaded action as edited ("Alterações não salvas" at every change of selection). "Edited" is therefore decided
    // by comparing what the editor holds now with what it held when the action was loaded.
    private string? _baseline;   // null = never saved (a new template or a duplicate): always counts as unsaved

    private string Fingerprint() => System.Text.Json.JsonSerializer.Serialize(ReadEditor());

    private bool HasUnsavedChanges() => _dirty && (_baseline is null || Fingerprint() != _baseline);

    private void BeginNew(StepKind? firstStep)
    {
        var blank = new ActionDefinition();
        if (firstStep is { } kind) blank.Steps.Add(StepCatalog.NewStep(kind));
        LoadEditor(blank);
        _loading = true;
        ActionList.SelectedItem = null;
        _loading = false;
        _dirty = true;   // a template is already something worth saving once it has a name
        _baseline = null;
        UpdateDerived();
        NameBox.Focus(FocusState.Programmatic);
    }

    /// <summary>Opens the window ready to define a new action (the launcher's "Nova ação" command).</summary>
    public async void StartNewAction()
    {
        if (await ConfirmLeaveAsync()) BeginNew(null);
    }


    /// <summary>
    /// Shows <paramref name="category"/> in the editable category box. Assigning Text alone is ignored when the
    /// value is one of the items, so the matching item is selected instead.
    /// </summary>
    private static void SetCategory(ComboBox box, string category)
    {
        var match = (box.ItemsSource as IEnumerable<string>)?.FirstOrDefault(c => c.Equals(category, StringComparison.CurrentCultureIgnoreCase));
        if (match is not null) { box.SelectedItem = match; return; }
        box.SelectedItem = null;
        box.Text = category;
    }

    private void OnEdited(object sender, object e)
    {
        if (_loading) return;
        _dirty = _baseline is null || Fingerprint() != _baseline;   // a late event that changed nothing is not an edit
        UpdateDerived();
    }

    private void OnCategorySubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args) => OnEdited(sender, args);

    private ActionDefinition ReadEditor()
    {
        var a = _current.Clone();
        a.Name = NameBox.Text;
        a.Category = CategoryBox.Text.Trim();
        a.Enabled = EnabledSwitch.IsOn;
        a.Hotkey = _current.Hotkey;
        a.Scope = (ScopeMode)Math.Max(0, ScopeBox.SelectedIndex);
        a.ScopeApps = ScopeAppsBox.Text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        a.OnError = (ErrorPolicy)Math.Max(0, OnErrorBox.SelectedIndex);
        a.Steps = Steps.Select(s => s.Step).ToList();
        return a;
    }

    private void UpdateDerived()
    {
        ScopeAppsPanel.Visibility = ScopeBox.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        StepsTitle.Text = Steps.Count > 1 ? $"Passos ({Steps.Count}) — macro" : "Passos";
        UpdateHotkeyHelp();

        var draft = ReadEditor();
        bool valid = _services.Actions.Validate(draft) is null;
        SaveButton.IsEnabled = _dirty && valid;
        DiscardButton.IsEnabled = _dirty;
    }

    private void UpdateHotkeyHelp()
    {
        var draft = ReadEditor();
        if (draft.Hotkey.Length == 0)
        {
            SetHelp("Sem atalho: a ação só aparece no launcher.", error: false);
            return;
        }
        if (_services.Actions.ValidateHotkey(draft) is { } problem)
        {
            SetHelp(problem, error: true);
            return;
        }
        var status = _current.Id != 0 && !_dirty ? _services.ActionHost?.StatusOf(_current.Id) : null;
        SetHelp(status is { Message.Length: > 0 } ? status.Message : "Atalho livre. Salve para ativá-lo.", error: status?.State == HotkeyState.Failed);
    }

    private void SetHelp(string text, bool error)
    {
        HotkeyHelp.Text = text;
        HotkeyHelp.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[error ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
    }

    // ----------------------------------------------------------- hotkey

    private void OnRecordHotkey(object sender, RoutedEventArgs e)
    {
        if (_recording) { StopRecording(); return; }
        _recording = true;
        // Registered hotkeys would swallow the very combination being recorded.
        _services.ActionHost?.SuspendHotkeys();
        _services.Host.UnregisterHotkey();
        RecordButton.Content = "Cancelar";
        HotkeyText.Text = "Pressione…";
        SetHelp("Pressione a combinação desejada (por exemplo Ctrl+Alt+P). Esc cancela.", error: false);
        RecordButton.Focus(FocusState.Programmatic);
    }

    private void StopRecording()
    {
        _menuGuardUntil = Environment.TickCount64 + 800;
        _recording = false;
        RecordButton.Content = "Gravar";
        HotkeyText.Text = _current.Hotkey.Length > 0 ? _current.Hotkey : "(sem atalho)";
        _services.ActionHost?.ResumeHotkeys();
        _services.ResumeHotkey();
        UpdateHotkeyHelp();
    }

    private void OnClearHotkey(object sender, RoutedEventArgs e)
    {
        if (_recording) StopRecording();
        _current.Hotkey = "";
        HotkeyText.Text = "(sem atalho)";
        OnEdited(sender, e);
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;

        uint vk = (uint)e.Key;
        if (HotkeySpec.IsModifierKey(vk)) return;

        var mods = HotkeyModifiers.None;
        if (IsDown(VirtualKey.Control)) mods |= HotkeyModifiers.Control;
        if (IsDown(VirtualKey.Menu)) mods |= HotkeyModifiers.Alt;
        if (IsDown(VirtualKey.Shift)) mods |= HotkeyModifiers.Shift;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) mods |= HotkeyModifiers.Win;

        if (e.Key == VirtualKey.Escape && mods == HotkeyModifiers.None) { StopRecording(); return; }

        var spec = new HotkeySpec(mods, vk);
        if (!spec.IsValid)
        {
            SetHelp("Use ao menos um modificador (Ctrl, Alt, Shift ou Win) junto com a tecla.", error: true);
            return;
        }
        _current.Hotkey = spec.ToString();
        StopRecording();
        OnEdited(this, e);
    }

    private void OnScopeChanged(object sender, SelectionChangedEventArgs e) => OnEdited(sender, e);

    private void OnRunningAppsOpening(object? sender, object e)
    {
        RunningAppsFlyout.Items.Clear();
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle != 0 && p.MainWindowTitle.Length > 0 && p.Id != Environment.ProcessId) names.Add(p.ProcessName);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            finally { p.Dispose(); }
        }
        foreach (var name in names)
        {
            var item = new MenuFlyoutItem { Text = name };
            item.Click += (_, _) =>
            {
                var current = ScopeAppsBox.Text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (!current.Contains(name, StringComparer.OrdinalIgnoreCase)) current.Add(name);
                ScopeAppsBox.Text = string.Join(", ", current);
            };
            RunningAppsFlyout.Items.Add(item);
        }
        if (names.Count == 0) RunningAppsFlyout.Items.Add(new MenuFlyoutItem { Text = "(nenhum aplicativo com janela)", IsEnabled = false });
    }

    // ------------------------------------------------------------- steps

    private void RenumberSteps()
    {
        for (int i = 0; i < Steps.Count; i++) Steps[i].Number = (i + 1).ToString();
    }

    // ------------------------------------------------------ macro recording

    private MacroRecordingSession? _macro;
    private RecordingBanner? _banner;
    private bool _macroAppend;

    private async void OnRecordIntoCurrent(object sender, RoutedEventArgs e) => await RecordMacroAsync(append: true);

    /// <summary>
    /// Explains what will be recorded, hides this window and starts listening to the keyboard. Pause (or the banner's
    /// button) stops; the steps then land in a new macro or at the end of the one being edited.
    /// </summary>
    private async Task RecordMacroAsync(bool append)
    {
        if (_macro is not null) return;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Gravar o teclado",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "Tudo o que você digitar em qualquer programa vira passos da macro: texto, atalhos, Enter, setas e as pausas. " +
                           "Para parar, pressione Pause (ou use o botão que aparece no alto da tela).\n\n" +
                           "Cuidado com senhas: campos de senha comuns do Windows são ignorados, mas os do navegador não são detectados. " +
                           "Nada é gravado enquanto o Apparatus estiver em primeiro plano.",
                },
                PrimaryButtonText = "Começar a gravar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            _macroAppend = append;
            _macro = new MacroRecordingSession(_services.Keyboard);
            _macro.StopRequested += () => DispatcherQueue.TryEnqueue(FinishMacroRecording);
            _banner = new RecordingBanner(() => _macro?.StepCount ?? 0, () => DispatcherQueue.TryEnqueue(FinishMacroRecording));
            ((OverlappedPresenter)AppWindow.Presenter).Minimize();
            _macro.Start();
            _banner.ShowAtTop();
        }
        catch (Exception ex)
        {
            AppLog.Error("Starting the macro recording failed", ex);
            AbortMacroRecording();
        }
    }

    private void FinishMacroRecording()
    {
        if (_macro is not { } session) return;
        var steps = session.Stop();
        int skipped = session.SkippedKeys, passwordKeys = session.PasswordKeysIgnored;
        AbortMacroRecording();

        ((OverlappedPresenter)AppWindow.Presenter).Restore();
        Activate();
        WindowHelpers.BringToForeground(_hwnd);

        if (steps.Count == 0)
        {
            ShowStatus("Nada foi gravado.", "Use o teclado em outro programa enquanto grava; o que você digita no Apparatus não entra.", InfoBarSeverity.Informational);
            return;
        }

        if (!_macroAppend)
        {
            BeginNew(null);
            NameBox.Text = "Macro gravada";
        }
        foreach (var step in steps) Steps.Add(new StepListItem(step));
        RenumberSteps();
        StepsList.SelectedIndex = Steps.Count - steps.Count;
        OnEdited(this, EventArgs.Empty);

        string notes = $"{steps.Count} passo(s) gravado(s). Confira, remova o que sobrar e use “Testar”.";
        if (skipped > 0) notes += $" {skipped} tecla(s) sem nome conhecido foram ignoradas.";
        if (passwordKeys > 0) notes += $" {passwordKeys} tecla(s) em campos de senha não foram gravadas.";
        ShowStatus("Gravação concluída", notes, InfoBarSeverity.Success);
    }

    private void AbortMacroRecording()
    {
        _macro?.Dispose();
        _macro = null;
        _banner?.Close();
        _banner = null;
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        Status.Severity = severity;
        Status.Title = title;
        Status.Message = message;
        Status.IsOpen = true;
    }

    private void AddStep(StepKind kind)
    {
        var item = new StepListItem(StepCatalog.NewStep(kind));
        Steps.Add(item);
        RenumberSteps();
        StepsList.SelectedItem = item;
        OnEdited(this, EventArgs.Empty);
    }

    private void OnStepSelected(object sender, SelectionChangedEventArgs e)
    {
        _editingStep = StepsList.SelectedItem as StepListItem;
        BuildStepEditor();
    }

    private void OnStepUp(object sender, RoutedEventArgs e) => MoveStep(-1);

    private void OnStepDown(object sender, RoutedEventArgs e) => MoveStep(+1);

    private void MoveStep(int delta)
    {
        if (StepsList.SelectedItem is not StepListItem item) return;
        int from = Steps.IndexOf(item), to = from + delta;
        if (to < 0 || to >= Steps.Count) return;
        Steps.Move(from, to);
        RenumberSteps();
        StepsList.SelectedItem = item;
        OnEdited(this, EventArgs.Empty);
    }

    private void OnStepDuplicate(object sender, RoutedEventArgs e)
    {
        if (StepsList.SelectedItem is not StepListItem item) return;
        var copy = new StepListItem(item.Step.Clone());
        Steps.Insert(Steps.IndexOf(item) + 1, copy);
        RenumberSteps();
        StepsList.SelectedItem = copy;
        OnEdited(this, EventArgs.Empty);
    }

    private void OnStepRemove(object sender, RoutedEventArgs e)
    {
        if (StepsList.SelectedItem is not StepListItem item) return;
        int index = Steps.IndexOf(item);
        Steps.Remove(item);
        RenumberSteps();
        if (Steps.Count > 0) StepsList.SelectedIndex = Math.Min(index, Steps.Count - 1);
        else { _editingStep = null; BuildStepEditor(); }
        OnEdited(this, EventArgs.Empty);
    }

    // ------------------------------------------------------ step editor

    private void BuildStepEditor()
    {
        StepForm.Children.Clear();
        if (_editingStep is null) { StepEditor.Visibility = Visibility.Collapsed; return; }

        var step = _editingStep.Step;
        var descriptor = StepCatalog.Get(step.Kind);
        StepEditorTitle.Text = descriptor.Title;
        StepEditorDescription.Text = descriptor.Description;
        foreach (var field in descriptor.Fields) StepForm.Children.Add(BuildField(field, step));

        _loading = true;
        ConditionBox.SelectedIndex = (int)(step.When?.Kind ?? ConditionKind.None);
        ConditionValueBox.Text = step.When?.Value ?? "";
        ConditionValueBox.Visibility = ConditionBox.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        ConditionValueBox.PlaceholderText = ConditionBox.SelectedIndex is 1 or 2 ? "nome do processo, por exemplo: chrome" : "texto do título";
        _loading = false;
        StepEditor.Visibility = Visibility.Visible;
    }

    private void StepChanged()
    {
        _editingStep?.Refresh();
        OnEdited(this, EventArgs.Empty);
    }

    private FrameworkElement BuildField(StepField field, ActionStep step)
    {
        string current = step.Get(field.Key, field.Default);

        switch (field.Type)
        {
            case FieldType.Bool:
            {
                var box = new CheckBox { Content = field.Label, IsChecked = current.Equals("true", StringComparison.OrdinalIgnoreCase) };
                RoutedEventHandler handler = (_, _) => { step.Args[field.Key] = box.IsChecked == true ? "true" : "false"; StepChanged(); };
                box.Checked += handler;
                box.Unchecked += handler;
                return box;
            }
            case FieldType.Number:
            {
                var box = new NumberBox
                {
                    Header = field.Label, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 220,
                    HorizontalAlignment = HorizontalAlignment.Left, Minimum = 0, Maximum = 600000,
                    Value = int.TryParse(current, out int n) ? n : 0,
                };
                box.ValueChanged += (_, args) =>
                {
                    if (double.IsNaN(args.NewValue)) return;
                    step.Args[field.Key] = ((int)args.NewValue).ToString();
                    StepChanged();
                };
                return WithHint(box, field.Hint);
            }
            case FieldType.Choice:
            {
                var combo = new ComboBox { Header = field.Label, HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var (value, label) in field.Choices ?? Array.Empty<(string, string)>())
                    combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
                combo.SelectedIndex = Math.Max(0, (field.Choices ?? Array.Empty<(string Value, string Label)>()).Select((c, i) => (c, i)).FirstOrDefault(x => x.c.Value == current).i);
                if (!step.Args.ContainsKey(field.Key) && combo.SelectedItem is ComboBoxItem first) step.Args[field.Key] = (string)first.Tag;
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem chosen) { step.Args[field.Key] = (string)chosen.Tag; StepChanged(); }
                };
                return combo;
            }
            case FieldType.Snippet:
            {
                var combo = new ComboBox { Header = field.Label, HorizontalAlignment = HorizontalAlignment.Stretch };
                var names = _services.Snippets.All.Select(s => s.Name).ToList();
                if (current.Length > 0 && !names.Contains(current, StringComparer.CurrentCultureIgnoreCase)) names.Insert(0, current);
                combo.ItemsSource = names;
                combo.SelectedItem = names.FirstOrDefault(n => n.Equals(current, StringComparison.CurrentCultureIgnoreCase));
                combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string s) { step.Args[field.Key] = s; StepChanged(); } };
                return combo;
            }
            case FieldType.File or FieldType.Folder or FieldType.FileOrFolder:
            {
                var text = new TextBox { Header = field.Label, Text = current, PlaceholderText = field.Hint ?? "" };
                text.TextChanged += (_, _) => { step.Args[field.Key] = text.Text; StepChanged(); };
                var grid = new Grid { ColumnSpacing = 8 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.Children.Add(text);

                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
                if (field.Type != FieldType.Folder) buttons.Children.Add(MakePicker("Arquivo…", async () => await PickFileAsync(), text));
                if (field.Type != FieldType.File) buttons.Children.Add(MakePicker("Pasta…", async () => await PickFolderAsync(), text));
                Grid.SetColumn(buttons, 1);
                grid.Children.Add(buttons);
                return grid;
            }
            default:
            {
                bool multi = field.Type == FieldType.MultiLine;
                var text = new TextBox
                {
                    Header = field.Label, Text = current, PlaceholderText = field.Hint ?? "",
                    AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    MinHeight = multi ? 130 : 0, IsSpellCheckEnabled = false,
                };
                if (multi) text.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas");
                text.TextChanged += (_, _) => { step.Args[field.Key] = text.Text; StepChanged(); };
                return text;
            }
        }
    }

    private static FrameworkElement WithHint(FrameworkElement control, string? hint)
    {
        if (string.IsNullOrEmpty(hint)) return control;
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(control);
        panel.Children.Add(new TextBlock { Text = hint, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private static Button MakePicker(string label, Func<Task<string?>> pick, TextBox target)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) =>
        {
            string? path = await pick();
            if (path is not null) target.Text = path;
        };
        return button;
    }

    private async Task<string?> PickFileAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private void OnConditionChanged(object sender, object e)
    {
        if (_loading || _editingStep is null) return;
        var kind = (ConditionKind)Math.Max(0, ConditionBox.SelectedIndex);
        ConditionValueBox.Visibility = kind == ConditionKind.None ? Visibility.Collapsed : Visibility.Visible;
        ConditionValueBox.PlaceholderText = kind is ConditionKind.ActiveProcessIs or ConditionKind.ActiveProcessIsNot
            ? "nome do processo, por exemplo: chrome"
            : "texto do título";
        _editingStep.Step.When = kind == ConditionKind.None ? null : new StepCondition { Kind = kind, Value = ConditionValueBox.Text };
        StepChanged();
    }

    // ------------------------------------------------------ save & co

    private bool TrySave()
    {
        try
        {
            var saved = _services.Actions.Save(ReadEditor());
            LoadEditor(saved.Clone());
            RefreshCategories();
            RefreshList(saved.Id);
            ShowStatus(saved.Hotkey.Length > 0 ? "Ação salva. O atalho já está valendo." : "Ação salva.", InfoBarSeverity.Success);
            return true;
        }
        catch (ActionValidationException ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
            return false;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e) => TrySave();

    private void OnDiscard(object sender, RoutedEventArgs e) =>
        LoadEditor(_current.Id == 0 ? new ActionDefinition() : _services.Actions.Get(_current.Id)?.Clone() ?? new ActionDefinition());

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        Status.Severity = severity;
        Status.Message = message;
        Status.IsOpen = true;
    }

    private async void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (_current.Id == 0 && !HasUnsavedChanges()) return;
        if (!await ConfirmLeaveAsync()) return;
        var copy = (_services.Actions.Get(_current.Id) ?? _current).Clone();
        copy.Id = 0;
        copy.Name += " (cópia)";
        copy.Hotkey = "";       // the same keys would conflict with the original
        copy.RunCount = 0;
        copy.LastRunUtc = null;
        LoadEditor(copy);
        _loading = true;
        ActionList.SelectedItem = null;
        _loading = false;
        _dirty = true;
        _baseline = null;   // not saved yet
        UpdateDerived();
        NameBox.Focus(FocusState.Programmatic);
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_current.Id == 0) { BeginNew(null); return; }
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "Excluir ação?",
            Content = $"“{_current.Name}” será removida e o atalho deixará de funcionar.",
            PrimaryButtonText = "Excluir", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _services.Actions.Delete(_current.Id);
        _dirty = false;
        if (Items.Count > 0) ActionList.SelectedIndex = 0;
        else BeginNew(null);
    }

    private async Task<bool> ConfirmLeaveAsync()
    {
        if (!HasUnsavedChanges())
        {
            _dirty = false;
            return true;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "Alterações não salvas",
            Content = "Deseja salvar as alterações desta ação?",
            PrimaryButtonText = "Salvar", SecondaryButtonText = "Descartar", CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary: return TrySave();
            case ContentDialogResult.Secondary: _dirty = false; return true;
            default: return false;
        }
    }

    // --------------------------------------------------------------- test

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var draft = ReadEditor();
        if (_services.Actions.Validate(draft) is { } problem) { ShowStatus(problem, InfoBarSeverity.Warning); return; }
        if (_services.ActionHost is not { } host) return;

        TestButton.IsEnabled = false;
        try
        {
            for (int i = 3; i >= 1; i--)
            {
                TestButton.Content = $"Executando em {i}…";
                await Task.Delay(1000);
            }
            TestButton.Content = "Executando…";
            await host.RunDefinitionAsync(draft);
            ShowStatus("Teste concluído. Se algo não aconteceu, veja o aviso da bandeja ou o log.", InfoBarSeverity.Informational);
        }
        finally
        {
            TestButton.Content = "Testar (em 3 s)";
            TestButton.IsEnabled = true;
        }
    }

    // ----------------------------------------------------- import/export

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var warning = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "Importar ações",
            Content = "Ações importadas podem executar programas e scripts no seu computador. Importe apenas arquivos de uma fonte em que você confia.",
            PrimaryButtonText = "Escolher arquivo…", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close,
        };
        if (await warning.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            string? path = null;
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeFilter.Add(".json");
            path = (await picker.PickSingleFileAsync())?.Path;
            if (path is null) return;

            var result = _services.Actions.ImportJson(await File.ReadAllTextAsync(path));
            string message = $"{result.Imported} nova(s), {result.Updated} atualizada(s).";
            if (result.Skipped.Count > 0) message += $" Ignoradas ({result.Skipped.Count}): " + string.Join("; ", result.Skipped.Take(3));
            ShowStatus(message, result.Skipped.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is ActionValidationException or IOException)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "acoes-apparatus" };
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await File.WriteAllTextAsync(file.Path, _services.Actions.ExportJson());
            ShowStatus($"{_services.Actions.All.Count} ação(ões) exportadas para {file.Path}.", InfoBarSeverity.Success);
        }
        catch (IOException ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }
}
