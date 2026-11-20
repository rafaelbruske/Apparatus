using Apparatus.Core.Search;

namespace Apparatus.Core.Snippets;

/// <summary>Finds snippets in the launcher by name, trigger, category, tag or content.</summary>
public sealed class SnippetsProvider : ISearchProvider
{
    private const int KindBonus = 85;
    private const int FavoriteBonus = 25;
    private const int ContentOnlyScore = 40;

    private readonly SnippetStore _store;

    public SnippetsProvider(SnippetStore store) => _store = store;

    public string Id => "snippets";

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchContext context, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        var seen = new HashSet<long>();

        foreach (var s in _store.All)
        {
            int best = FuzzyMatcher.Score(s.Name, context.Fuzzy);
            if (s.Trigger.Length > 0)
            {
                // Typing the trigger itself (";email") should find its snippet even when it is not in the name.
                int t = FuzzyMatcher.Score(s.Trigger, context.Fuzzy);
                if (t != FuzzyMatcher.NoMatch) best = Math.Max(best, t + 20);
            }
            int meta = FuzzyMatcher.Score(s.Category + " " + string.Join(' ', s.Tags), context.Fuzzy);
            if (meta != FuzzyMatcher.NoMatch) best = Math.Max(best, meta - 30);

            if (best == FuzzyMatcher.NoMatch) continue;
            seen.Add(s.Id);
            results.Add(ToResult(s, best + KindBonus + (s.Favorite ? FavoriteBonus : 0)));
        }

        if (context.Fuzzy.TotalLength >= 3)
        {
            foreach (var s in _store.SearchContent(context.Raw, 12))
            {
                if (seen.Add(s.Id)) results.Add(ToResult(s, ContentOnlyScore + KindBonus + (s.Favorite ? FavoriteBonus : 0)));
            }
        }
        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    public static SearchResult ToResult(Snippet s, double score = 0)
    {
        string subtitle = s.Trigger.Length > 0 ? $"{s.Trigger}  ·  {s.Preview(70)}" : s.Preview(80);
        return new SearchResult($"snippet:{s.Id}", ResultKind.Snippet, s.Name, subtitle, s.Id.ToString(), score);
    }
}
