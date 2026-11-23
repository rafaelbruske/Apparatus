using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apparatus.Core.Assistant;

/// <summary>One provider the user can pick. The list lives in assistant-providers.json so it can be updated without touching C#.</summary>
public sealed record ProviderInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("baseUrl")] string BaseUrl,
    [property: JsonPropertyName("needsKey")] bool NeedsKey,
    [property: JsonPropertyName("defaultModel")] string DefaultModel,
    [property: JsonPropertyName("keyUrl")] string KeyUrl,
    [property: JsonPropertyName("freeTier")] string FreeTier,
    [property: JsonPropertyName("badge")] string Badge)
{
    /// <summary>The "custom" provider has no fixed address: the user types it.</summary>
    public bool IsCustom => BaseUrl.Length == 0;
}

public sealed class ProviderCatalog
{
    private readonly List<ProviderInfo> _providers;

    public ProviderCatalog(IEnumerable<ProviderInfo> providers) => _providers = providers.ToList();

    public IReadOnlyList<ProviderInfo> All => _providers;

    public ProviderInfo? Find(string? id) =>
        _providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The providers bundled with the app.</summary>
    public static ProviderCatalog LoadEmbedded()
    {
        using var stream = typeof(ProviderCatalog).Assembly.GetManifestResourceStream("assistant-providers.json")
            ?? throw new InvalidOperationException("assistant-providers.json is not embedded");
        return Parse(stream);
    }

    public static ProviderCatalog Parse(Stream json)
    {
        var list = JsonSerializer.Deserialize<List<ProviderInfo>>(json, new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) ?? new();

        // A broken entry must not take the whole screen down: skip what has no id or no usable address.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valid = new List<ProviderInfo>();
        foreach (var p in list)
        {
            if (string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Name) || !seen.Add(p.Id)) continue;
            if (!p.IsCustom && AssistantEndpoint.Validate(p.BaseUrl, out _) is not null) continue;
            valid.Add(p);
        }
        return new ProviderCatalog(valid);
    }
}
