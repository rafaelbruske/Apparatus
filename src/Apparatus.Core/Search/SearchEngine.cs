using Apparatus.Core.Storage;

namespace Apparatus.Core.Search;

/// <summary>
/// Runs every provider concurrently, applies history-based boosts and returns the best results.
/// With an empty query it returns the most relevant history instead.
/// </summary>
public sealed class SearchEngine
{
    private readonly IReadOnlyList<ISearchProvider> _providers;
    private readonly UsageStore _usage;

    public SearchEngine(IEnumerable<ISearchProvider> providers, UsageStore usage)
    {
        _providers = providers.ToList();
        _usage = usage;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string rawQuery, int maxResults, CancellationToken ct)
    {
        var context = SearchContext.Create(rawQuery);
        if (context.Fuzzy.IsEmpty && context.Raw.Length == 0) return FavoritesThenRecent(maxResults);

        var tasks = _providers.Select(p => RunProvider(p, context, ct)).ToArray();
        var all = await Task.WhenAll(tasks).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var best = new Dictionary<string, SearchResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in all.SelectMany(x => x))
        {
            bool favorite = _usage.IsFavorite(r.Key);
            var boosted = r with { Score = r.Score + _usage.Boost(r.Key, context.Raw) + (favorite ? FavoriteBoost : 0), Favorite = favorite || r.Favorite };
            if (!best.TryGetValue(boosted.Key, out var existing) || existing.Score < boosted.Score) best[boosted.Key] = boosted;
        }

        return best.Values
            .OrderByDescending(r => r.Score)
            .Take(maxResults)
            .Select(r => r.Highlight is null && r.Kind is not (ResultKind.Calculator or ResultKind.Web or ResultKind.Url)
                ? r with { Highlight = FuzzyMatcher.Highlight(r.Title, context.Fuzzy) }
                : r)
            .ToList();
    }

    private const double FavoriteBoost = 50;

    /// <summary>What the launcher shows before anything is typed: pinned items, then what was used most.</summary>
    private IReadOnlyList<SearchResult> FavoritesThenRecent(int max)
    {
        var favorites = _usage.Favorites();
        var keys = favorites.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recent = _usage.Recent(max + keys.Count).Where(r => !keys.Contains(r.Key));
        return favorites.Concat(recent).Take(Math.Max(max, favorites.Count)).ToList();
    }

    private static async Task<IReadOnlyList<SearchResult>> RunProvider(ISearchProvider provider, SearchContext context, CancellationToken ct)
    {
        try
        {
            return await provider.QueryAsync(context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Provider '{provider.Id}' failed", ex);
            return Array.Empty<SearchResult>();
        }
    }

    public void RecordUse(string query, SearchResult result) => _usage.Record(query, result);
}
