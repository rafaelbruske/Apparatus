using Apparatus.Core.Search;
using Apparatus.Core.Search.Providers;

namespace Apparatus.Core.Actions;

/// <summary>One installed application offered while the target of an "open application" step is typed.</summary>
/// <param name="Name">The name Windows shows in its Start menu.</param>
/// <param name="Target">What the step stores and Windows opens: a program's path or "shell:AppsFolder\…".</param>
/// <param name="IconKey">The key of its icon (see <see cref="AppEntry.IconKey"/>), or null when there is none to be had.</param>
public sealed record AppChoice(string Name, string Target, string? IconKey = null)
{
    public override string ToString() => Name;
}

/// <summary>Finds installed applications by name, for the box that takes either a name or a path.</summary>
public static class AppSuggestions
{
    public const int MaxChoices = 8;

    /// <summary>
    /// The applications that match what is being typed, best first. Nothing for an empty box, nor for what is clearly a path or an
    /// address ("C:\…", "\\server", "shell:…"): the user is writing the target itself.
    /// </summary>
    public static IReadOnlyList<AppChoice> Find(IEnumerable<AppEntry> apps, string? typed, int max = MaxChoices)
    {
        string text = (typed ?? "").Trim();
        if (text.Length == 0 || LooksLikeTarget(text)) return Array.Empty<AppChoice>();

        var query = FuzzyQuery.Parse(text);
        return apps.Select(app => (App: app, Score: FuzzyMatcher.Score(app.Name, query)))
            .Where(x => x.Score != FuzzyMatcher.NoMatch)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .Select(x => new AppChoice(x.App.Name, x.App.ParsingName, x.App.IconKey))
            .ToList();
    }

    /// <summary>The name of the installed application a target opens, or null when it is not one of them.</summary>
    public static string? NameOf(IEnumerable<AppEntry> apps, string? target)
    {
        string text = (target ?? "").Trim();
        if (text.Length == 0) return null;
        return apps.FirstOrDefault(a => a.ParsingName.Equals(text, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    private static bool LooksLikeTarget(string text) =>
        text.Contains('\\') || text.Contains('/') || text.StartsWith('"') || text.StartsWith('%')
        || text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
        || (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':');
}
