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
        _ => "",
    };
}
