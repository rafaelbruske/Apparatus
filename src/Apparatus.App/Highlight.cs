using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Apparatus.App;

/// <summary>Attached property that renders a result title with the matched characters in bold.</summary>
public static class Highlight
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.RegisterAttached(
        "Item", typeof(ResultItemViewModel), typeof(Highlight), new PropertyMetadata(null, OnItemChanged));

    public static ResultItemViewModel? GetItem(DependencyObject d) => (ResultItemViewModel?)d.GetValue(ItemProperty);

    public static void SetItem(DependencyObject d, ResultItemViewModel? value) => d.SetValue(ItemProperty, value);

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        block.Inlines.Clear();
        if (e.NewValue is not ResultItemViewModel item) return;

        string title = item.Title;
        var marks = item.Highlight is { Count: > 0 } h ? new HashSet<int>(h) : null;
        if (marks is null)
        {
            block.Inlines.Add(new Run { Text = title });
            return;
        }

        int start = 0;
        while (start < title.Length)
        {
            bool bold = marks.Contains(start);
            int end = start + 1;
            while (end < title.Length && marks.Contains(end) == bold) end++;
            var run = new Run { Text = title[start..end] };
            if (bold) run.FontWeight = FontWeights.Bold;
            block.Inlines.Add(run);
            start = end;
        }
    }
}
