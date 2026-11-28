using Apparatus.Core.Search;

namespace Apparatus.Core.Actions;

/// <summary>Lets the launcher find and run actions and macros by name or category.</summary>
public sealed class ActionsProvider : ISearchProvider
{
    private const int KindBonus = 80;

    private readonly ActionStore _store;
    private readonly Func<string, string?>? _appName;

    /// <param name="appName">Names the installed application a step opens (see <see cref="StepCatalog.Summarize"/>).</param>
    public ActionsProvider(ActionStore store, Func<string, string?>? appName = null)
    {
        _store = store;
        _appName = appName;
    }

    public string Id => "actions";

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchContext context, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        foreach (var a in _store.All)
        {
            if (!a.Enabled) continue;
            int best = FuzzyMatcher.Score(a.Name, context.Fuzzy);
            int meta = FuzzyMatcher.Score(a.Category, context.Fuzzy);
            if (meta != FuzzyMatcher.NoMatch) best = Math.Max(best, meta - 30);
            if (best == FuzzyMatcher.NoMatch) continue;
            results.Add(ToResult(a, best + KindBonus, _appName));
        }
        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    public static SearchResult ToResult(ActionDefinition a, double score = 0, Func<string, string?>? appName = null)
    {
        string subtitle = a.Hotkey.Length > 0 ? $"{a.Hotkey}  ·  {a.Describe(70, appName)}" : a.Describe(80, appName);
        return new SearchResult($"action:{a.Id}", ResultKind.Action, a.Name, subtitle, a.Id.ToString(), score);
    }
}
