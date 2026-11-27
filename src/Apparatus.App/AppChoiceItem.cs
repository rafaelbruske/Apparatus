using System.ComponentModel;
using Apparatus.Core.Actions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Apparatus.App;

/// <summary>A suggestion of the application box: the installed application and its icon, which may arrive a moment later.</summary>
public sealed class AppChoiceItem : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public AppChoiceItem(AppChoice choice) => Choice = choice;

    public AppChoice Choice { get; }

    public string Name => Choice.Name;

    public string Target => Choice.Target;

    /// <summary>The real icon once loaded; until then (or if none exists) a generic glyph shows in its place.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlyphVisibility)));
        }
    }

    public Visibility GlyphVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Name;   // what screen readers say
}
