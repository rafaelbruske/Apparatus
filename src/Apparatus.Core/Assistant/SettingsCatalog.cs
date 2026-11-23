using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apparatus.Core.Assistant;

public sealed record SettingsPage(string Name, string Uri);

/// <summary>
/// The Windows pages the assistant may point to: the same curated list the launcher searches (windows-settings.json).
/// It is both what goes into the prompt and the allow-list a tool call is checked against, so the model can never
/// make the app open something that is not in it.
/// </summary>
public sealed class SettingsCatalog
{
    private sealed record Entry(
        [property: JsonPropertyName("n")] string[] N,
        [property: JsonPropertyName("u")] string U);

    private readonly Dictionary<string, SettingsPage> _byUri;

    public SettingsCatalog(IEnumerable<SettingsPage> pages)
    {
        Pages = pages.ToList();
        _byUri = new Dictionary<string, SettingsPage>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Pages) _byUri.TryAdd(p.Uri, p);
    }

    public IReadOnlyList<SettingsPage> Pages { get; }

    /// <summary>The page for an exact catalog URI (case-insensitive), or null.</summary>
    public SettingsPage? Find(string? uri) => uri is not null && _byUri.TryGetValue(uri.Trim(), out var page) ? page : null;

    /// <summary>"uri — name" lines for the prompt.</summary>
    public string ToPromptLines()
    {
        var sb = new StringBuilder();
        foreach (var p in Pages) sb.Append(p.Uri).Append(" — ").Append(p.Name).Append('\n');
        return sb.ToString().TrimEnd();
    }

    public static SettingsCatalog LoadEmbedded()
    {
        using var stream = typeof(SettingsCatalog).Assembly.GetManifestResourceStream("windows-settings.json")
            ?? throw new InvalidOperationException("windows-settings.json is not embedded");
        return Parse(stream);
    }

    public static SettingsCatalog Parse(Stream json)
    {
        var raw = JsonSerializer.Deserialize<List<Entry>>(json) ?? new();
        return new SettingsCatalog(raw
            .Where(e => e.N is { Length: > 0 } && !string.IsNullOrWhiteSpace(e.U))
            .Select(e => new SettingsPage(e.N[0], e.U)));
    }
}
