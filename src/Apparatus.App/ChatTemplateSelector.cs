using Apparatus.Core.Assistant;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Apparatus.App;

/// <summary>Picks the look of a conversation line from its role: the user's question, the answer, a remark or an error.</summary>
public sealed class ChatTemplateSelector : DataTemplateSelector
{
    public DataTemplate? User { get; set; }
    public DataTemplate? Assistant { get; set; }
    public DataTemplate? Notice { get; set; }
    public DataTemplate? Error { get; set; }
    public DataTemplate? Suggestion { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is ChatEntry e
        ? e.Role switch
        {
            ChatEntryRole.User => User,
            ChatEntryRole.Assistant => Assistant,
            ChatEntryRole.Error => Error,
            ChatEntryRole.Suggestion => Suggestion,
            _ => Notice,
        }
        : Notice;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
