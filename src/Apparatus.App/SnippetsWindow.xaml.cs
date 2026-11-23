using System.Collections.ObjectModel;
using Apparatus.Core;
using Apparatus.Core.Search;
using Apparatus.Core.Snippets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Apparatus.App;

public sealed class SnippetListItem
{
    public SnippetListItem(Snippet snippet)
    {
        Snippet = snippet;
        Name = snippet.Name;
        Subtitle = snippet.Trigger.Length > 0 ? $"{snippet.Trigger}  ·  {snippet.Preview(40)}" : snippet.Preview(50);
        FavoriteVisibility = snippet.Favorite ? Visibility.Visible : Visibility.Collapsed;
    }

    public Snippet Snippet { get; }
    public string Name { get; }
    public string Subtitle { get; }
    public Visibility FavoriteVisibility { get; }
}

public sealed partial class SnippetsWindow : Window
{
    private const string AllCategories = "Todas as categorias";

    private readonly AppServices _services;
    private readonly nint _hwnd;
    private Snippet _current = new();
    private bool _loading;
    private bool _dirty;
    private bool _revertingSelection;
    private readonly (string Label, string Text)[] _variables =
    {
        ("Data de hoje  {date}", "{date}"),
        ("Hora  {time}", "{time}"),
        ("Data e hora  {datetime}", "{datetime}"),
        ("Data daqui a 7 dias  {date+7d}", "{date+7d}"),
        ("Data com formato  {date:dddd, dd 'de' MMMM}", "{date:dddd, dd 'de' MMMM}"),
        ("Área de transferência  {clipboard}", "{clipboard}"),
        ("Posição do cursor  {cursor}", "{cursor}"),
        ("Campo a preencher  {field:Nome}", "{field:Nome}"),
        ("Campo com valor padrão  {field:Assunto=Reunião}", "{field:Assunto=Reunião}"),
        ("Lista de opções  {choice:Tom=formal|informal}", "{choice:Tom=formal|informal}"),
        ("Usuário do Windows  {user}", "{user}"),
        ("Identificador único  {uuid}", "{uuid}"),
    };

    public ObservableCollection<SnippetListItem> Items { get; } = new();

    public SnippetsWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        AppWindow.SetIcon(AppPaths.AssetPath("apparatus.ico"));
        double scale = Native.WindowHelpers.GetScale(_hwnd);
        AppWindow.Resize(new SizeInt32((int)(1180 * scale), (int)(860 * scale)));

        foreach (var (label, text) in _variables)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => InsertText(text);
            VariablesFlyout.Items.Add(item);
        }

        _services.Snippets.Changed += OnStoreChanged;
        Closed += (_, _) => _services.Snippets.Changed -= OnStoreChanged;

        RefreshCategories();
        RefreshList(selectId: null);
        if (Items.Count > 0) SnippetList.SelectedIndex = 0;
        else BeginNew();
    }

    // ------------------------------------------------------------- list

    private void OnStoreChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        RefreshCategories();
        RefreshList(_current.Id == 0 ? null : _current.Id);
    });

    private void RefreshCategories()
    {
        string? selected = CategoryFilter.SelectedItem as string;
        var categories = new List<string> { AllCategories };
        categories.AddRange(_services.Snippets.Categories);
        CategoryFilter.ItemsSource = categories;
        CategoryFilter.SelectedItem = selected is not null && categories.Contains(selected) ? selected : AllCategories;

        // The editor's category box offers the existing categories too.
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
        bool filterCategory = category is not null && category != AllCategories;

        IEnumerable<Snippet> source = _services.Snippets.All;
        if (filterCategory) source = source.Where(s => s.Category.Equals(category, StringComparison.CurrentCultureIgnoreCase));

        List<Snippet> shown;
        if (query.IsEmpty)
        {
            shown = source.OrderByDescending(s => s.Favorite).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        else
        {
            var contentHits = _services.Snippets.SearchContent(SearchBox.Text, 50).Select(s => s.Id).ToHashSet();
            shown = source
                .Select(s => (s, score: Best(s, query, contentHits)))
                .Where(x => x.score != FuzzyMatcher.NoMatch)
                .OrderByDescending(x => x.score)
                .Select(x => x.s)
                .ToList();
        }

        _loading = true;
        Items.Clear();
        foreach (var s in shown) Items.Add(new SnippetListItem(s));
        if (selectId is { } id) SnippetList.SelectedItem = Items.FirstOrDefault(i => i.Snippet.Id == id);
        _loading = false;
    }

    private static int Best(Snippet s, FuzzyQuery q, HashSet<long> contentHits)
    {
        int best = Math.Max(FuzzyMatcher.Score(s.Name, q), FuzzyMatcher.Score(s.Trigger, q));
        int meta = FuzzyMatcher.Score(s.Category + " " + string.Join(' ', s.Tags), q);
        if (meta != FuzzyMatcher.NoMatch) best = Math.Max(best, meta - 30);
        if (best == FuzzyMatcher.NoMatch && contentHits.Contains(s.Id)) best = 10;
        return best;
    }

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _revertingSelection) return;
        if (SnippetList.SelectedItem is not SnippetListItem item) return;
        if (item.Snippet.Id == _current.Id) return;

        if (!await ConfirmLeaveAsync())
        {
            // Put the selection back where it was.
            _revertingSelection = true;
            SnippetList.SelectedItem = Items.FirstOrDefault(i => i.Snippet.Id == _current.Id);
            _revertingSelection = false;
            return;
        }
        LoadEditor(item.Snippet.Clone());
    }

    // ----------------------------------------------------------- editor

    private void LoadEditor(Snippet snippet)
    {
        _loading = true;
        _current = snippet;
        NameBox.Text = snippet.Name;
        TriggerBox.Text = snippet.Trigger;
        AutoExpandSwitch.IsOn = snippet.AutoExpand;
        SetCategory(CategoryBox, snippet.Category);
        TagsBox.Text = string.Join(", ", snippet.Tags);
        ModeBox.SelectedIndex = (int)snippet.Mode;
        FavoriteSwitch.IsOn = snippet.Favorite;
        ContentBox.Text = snippet.Content;
        Status.IsOpen = false;
        _dirty = false;
        _baseline = Fingerprint();
        _loading = false;
        UpdateDerived();
    }

    // WinUI raises TextChanged later than the assignment that caused it, after _loading is already false again. Those late events
    // used to mark every freshly loaded snippet as edited, and "Alterações não salvas" then appeared at every change of selection.
    // So "edited" is decided by comparing what the controls hold now with what they held when the snippet was loaded.
    private string? _baseline;   // null = never saved (a duplicate): always counts as unsaved

    private string Fingerprint() => string.Join("\u001f",
        NameBox.Text, TriggerBox.Text.Trim(), AutoExpandSwitch.IsOn, CategoryBox.Text.Trim(),
        string.Join(",", TagsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
        ModeBox.SelectedIndex, FavoriteSwitch.IsOn, ContentBox.Text);

    private bool HasUnsavedChanges() => _dirty && (_baseline is null || Fingerprint() != _baseline);

    private void BeginNew()
    {
        var blank = new Snippet { Name = "", AutoExpand = true };
        LoadEditor(blank);
        _loading = true;
        SnippetList.SelectedItem = null;
        _loading = false;
        NameBox.Focus(FocusState.Programmatic);
    }

    /// <summary>Opens the window ready to type a new snippet (used by the "Novo snippet" command).</summary>
    /// <param name="draft">A snippet to start from (the assistant's proposal). It is shown as unsaved; nothing is stored until the user saves.</param>
    public async void StartNewSnippet(Snippet? draft = null)
    {
        if (!await ConfirmLeaveAsync()) return;
        if (draft is null) { BeginNew(); return; }

        LoadEditor(draft.Clone());
        _loading = true;
        SnippetList.SelectedItem = null;
        _loading = false;
        _dirty = true;
        _baseline = null;   // never saved: counts as unsaved
        UpdateDerived();
        NameBox.Focus(FocusState.Programmatic);
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

    private void OnCategorySubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        // The text typed into the editable box is already in sender.Text; just mark it as an edit.
        OnEdited(sender, args);
    }

    private void UpdateDerived()
    {
        // Trigger feedback, live.
        string trigger = TriggerBox.Text.Trim();
        string? problem = _services.Snippets.ValidateTrigger(trigger, _current.Id == 0 ? null : _current.Id);
        TriggerHelp.Text = problem ?? (trigger.Length == 0 ? "Sem gatilho: só aparece no launcher." : "Gatilho livre.");
        TriggerHelp.Foreground = problem is null
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        AutoExpandSwitch.IsEnabled = trigger.Length > 0;

        // Preview.
        var template = SnippetTemplate.Parse(ContentBox.Text);
        var rendered = template.Render(SnippetContext.Create("(texto da área de transferência)"));
        string preview = rendered.Text;
        if (rendered.CursorFromEnd > 0 && preview.Length >= rendered.CursorFromEnd)
        {
            // Show where the caret will land.
            int at = preview.Length - 1;
            int steps = rendered.CursorFromEnd;
            var enumerator = System.Globalization.StringInfo.ParseCombiningCharacters(preview);
            at = enumerator.Length >= steps ? enumerator[enumerator.Length - steps] : 0;
            preview = preview.Insert(at, "▏");
        }
        PreviewText.Text = preview.Length == 0 ? "(vazio)" : preview;
        FieldsText.Text = template.Fields.Count == 0
            ? ""
            : "Campos pedidos ao inserir: " + string.Join(", ", template.Fields.Select(f => f.IsChoice ? $"{f.Label} (lista)" : f.Label));

        SaveButton.IsEnabled = _dirty && problem is null && NameBox.Text.Trim().Length > 0;
        DiscardButton.IsEnabled = _dirty;
    }

    private void InsertText(string text)
    {
        ContentBox.Focus(FocusState.Programmatic);
        ContentBox.SelectedText = text;
        ContentBox.Select(ContentBox.SelectionStart + ContentBox.SelectionLength, 0);
    }

    private Snippet ReadEditor()
    {
        var s = _current.Clone();
        s.Name = NameBox.Text;
        s.Trigger = TriggerBox.Text.Trim();
        s.AutoExpand = AutoExpandSwitch.IsOn;
        s.Category = CategoryBox.Text.Trim();
        s.Tags = TagsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        s.Mode = (InsertMode)Math.Max(0, ModeBox.SelectedIndex);
        s.Favorite = FavoriteSwitch.IsOn;
        s.Content = ContentBox.Text;
        return s;
    }

    private bool TrySave()
    {
        try
        {
            var saved = _services.Snippets.Save(ReadEditor());
            LoadEditor(saved.Clone());
            RefreshCategories();
            RefreshList(saved.Id);
            ShowStatus("Snippet salvo.", InfoBarSeverity.Success);
            return true;
        }
        catch (SnippetValidationException ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
            return false;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e) => TrySave();

    private void OnDiscard(object sender, RoutedEventArgs e)
    {
        var original = _current.Id == 0 ? new Snippet() : _services.Snippets.Get(_current.Id)?.Clone() ?? new Snippet();
        LoadEditor(original);
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        Status.Severity = severity;
        Status.Message = message;
        Status.IsOpen = true;
    }

    // ---------------------------------------------------------- actions

    private async void OnNew(object sender, RoutedEventArgs e)
    {
        if (await ConfirmLeaveAsync()) BeginNew();
    }

    private async void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (_current.Id == 0 && !HasUnsavedChanges()) return;
        if (!await ConfirmLeaveAsync()) return;
        var copy = _services.Snippets.Get(_current.Id)?.Clone() ?? _current.Clone();
        copy.Id = 0;
        copy.Name += " (cópia)";
        copy.Trigger = "";           // triggers must be unique
        copy.UseCount = 0;
        copy.LastUsedUtc = null;
        LoadEditor(copy);
        _loading = true;
        SnippetList.SelectedItem = null;
        _loading = false;
        _dirty = true;
        _baseline = null;   // not saved yet
        UpdateDerived();
        NameBox.Focus(FocusState.Programmatic);
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_current.Id == 0) { BeginNew(); return; }
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Excluir snippet?",
            Content = $"“{_current.Name}” será removido. Isso não pode ser desfeito.",
            PrimaryButtonText = "Excluir",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _services.Snippets.Delete(_current.Id);
        _dirty = false;
        if (Items.Count > 0) SnippetList.SelectedIndex = 0;
        else BeginNew();
    }

    /// <summary>Asks what to do with unsaved edits. True means it is fine to move on.</summary>
    private async Task<bool> ConfirmLeaveAsync()
    {
        if (!HasUnsavedChanges())
        {
            _dirty = false;
            return true;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Alterações não salvas",
            Content = "Deseja salvar as alterações deste snippet?",
            PrimaryButtonText = "Salvar",
            SecondaryButtonText = "Descartar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary: return TrySave();
            case ContentDialogResult.Secondary: _dirty = false; return true;
            default: return false;
        }
    }

    // ---------------------------------------------------- import/export

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var result = _services.Snippets.ImportJson(await File.ReadAllTextAsync(file.Path));
            string message = $"{result.Imported} novo(s), {result.Updated} atualizado(s).";
            if (result.Skipped.Count > 0) message += $" Ignorados ({result.Skipped.Count}): " + string.Join("; ", result.Skipped.Take(3));
            ShowStatus(message, result.Skipped.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is SnippetValidationException or IOException)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "snippets-apparatus" };
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await File.WriteAllTextAsync(file.Path, _services.Snippets.ExportJson());
            ShowStatus($"{_services.Snippets.All.Count} snippet(s) exportados para {file.Path}.", InfoBarSeverity.Success);
        }
        catch (IOException ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }
}
