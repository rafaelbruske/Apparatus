using System.ComponentModel;
using System.Runtime.CompilerServices;
using Apparatus.Core.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Apparatus.App;

public sealed class ResultItemViewModel : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public ResultItemViewModel(SearchResult result)
    {
        Result = result;
        Glyph = GlyphFor(result.Kind);
        KindLabel = LabelFor(result.Kind);
    }

    public SearchResult Result { get; }

    public string Title => Result.Title;

    public string Subtitle => Result.Subtitle;

    public IReadOnlyList<int>? Highlight => Result.Highlight;

    public string Glyph { get; }

    /// <summary>Initial shown in the "all apps" list on the first app of each letter; empty elsewhere.</summary>
    public string Letter { get; set; } = "";

    /// <summary>A small heading shown above this row in the Start menu (a section name, "Usados recentemente"); empty elsewhere.</summary>
    public string Header { get; set; } = "";

    public Visibility HeaderVisibility => Header.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The app has jump list tasks ("New window"...): a small button opens them.</summary>
    public bool HasTasks { get; set; }

    public Visibility TasksVisibility => HasTasks ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The id of an app result (its AppUserModelID or path), which names its jump list; null for anything else.</summary>
    public string? AppId => Result.Kind == ResultKind.App && Result.Key.StartsWith("app:", StringComparison.Ordinal) ? Result.Key[4..] : null;

    public Visibility FavoriteVisibility => Result.Favorite ? Visibility.Visible : Visibility.Collapsed;

    public string KindLabel { get; }

    /// <summary>The real icon once loaded; until then (or if none exists) the kind glyph shows.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GlyphVisibility));
        }
    }

    public Visibility GlyphVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // Segoe Fluent Icons code points.
    private static string GlyphFor(ResultKind kind) => kind switch
    {
        ResultKind.App => "",
        ResultKind.File => "",
        ResultKind.Folder => "",
        ResultKind.Setting => "",
        ResultKind.Web or ResultKind.Url => "",
        ResultKind.Calculator => "",
        ResultKind.Command => "",
        ResultKind.Snippet => "",
        ResultKind.Action => "",
        ResultKind.Extension => "",
        ResultKind.Clipboard => "",
        _ => "",
    };

    private static string LabelFor(ResultKind kind) => kind switch
    {
        ResultKind.App => "Aplicativo",
        ResultKind.File => "Arquivo",
        ResultKind.Folder => "Pasta",
        ResultKind.Setting => "Configuração",
        ResultKind.Web or ResultKind.Url => "Web",
        ResultKind.Calculator => "Cálculo",
        ResultKind.Command => "Comando",
        ResultKind.Snippet => "Snippet",
        ResultKind.Action => "Ação",
        ResultKind.Extension => "Extensão",
        ResultKind.Clipboard => "Copiado",
        _ => "",
    };
}
