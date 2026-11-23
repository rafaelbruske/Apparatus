using Microsoft.UI.Xaml;

namespace Apparatus.App;

/// <summary>Small helpers for x:Bind in the conversation's templates.</summary>
public static class ChatView
{
    public static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
