namespace Apparatus.Core.Search;

public enum ResultKind
{
    App = 0,
    File = 1,
    Folder = 2,
    Setting = 3,
    Web = 4,
    Url = 5,
    Calculator = 6,
    Command = 7,
}

/// <summary>
/// One row in the launcher. <see cref="Key"/> identifies it across sessions (frecency, history);
/// <see cref="Payload"/> is what gets executed (AUMID, path, URI, expression result, command id).
/// <see cref="Icon"/> is "shell:&lt;parsing name&gt;" or "path:&lt;file&gt;" for real icons; null means "use the kind glyph".
/// </summary>
public sealed record SearchResult(
    string Key,
    ResultKind Kind,
    string Title,
    string Subtitle,
    string Payload,
    double Score = 0,
    string? Icon = null,
    IReadOnlyList<int>? Highlight = null);

public sealed record SearchContext(string Raw, FuzzyQuery Fuzzy)
{
    public static SearchContext Create(string raw) => new(raw.Trim(), FuzzyQuery.Parse(raw));
}

public interface ISearchProvider
{
    string Id { get; }

    /// <summary>Must be fast (the launcher queries on every keystroke) and honour cancellation.</summary>
    Task<IReadOnlyList<SearchResult>> QueryAsync(SearchContext context, CancellationToken cancellationToken);
}
