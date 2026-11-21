using Apparatus.Core.Actions;
using Apparatus.Core.Clipboard;
using Apparatus.Core.Search;
using Apparatus.Core.Search.Providers;
using Apparatus.Core.Settings;
using Apparatus.Core.Storage;

namespace Apparatus.Core.Tests;

public class FavoritesTests
{
    private static SearchResult R(string key, string title = "T", ResultKind kind = ResultKind.App) =>
        new(key, kind, title, "sub", "payload", 10, "shell:x");

    private sealed class Fixed : ISearchProvider
    {
        private readonly SearchResult[] _results;
        public Fixed(params SearchResult[] results) => _results = results;
        public string Id => "fixed";
        public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchContext c, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchResult>>(_results);
    }

    [Fact]
    public void Toggle_PinsAndUnpins_InOrder()
    {
        using var store = new UsageStore(":memory:");
        Assert.True(store.ToggleFavorite(R("a", "A")));
        Assert.True(store.ToggleFavorite(R("b", "B")));
        Assert.True(store.IsFavorite("a"));
        Assert.Equal(new[] { "A", "B" }, store.Favorites().Select(f => f.Title).ToArray());
        Assert.All(store.Favorites(), f => Assert.True(f.Favorite));

        Assert.False(store.ToggleFavorite(R("a")));
        Assert.False(store.IsFavorite("a"));
        Assert.Equal(new[] { "B" }, store.Favorites().Select(f => f.Title).ToArray());
    }

    [Fact]
    public void Favorites_SurviveReopen_AndKeepEverythingNeededToRunThem()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "u.db");
        using (var store = new UsageStore(path))
        {
            store.ToggleFavorite(new SearchResult("file:C:\\x.txt", ResultKind.File, "x.txt", "C:\\", "C:\\x.txt", 99, "path:C:\\x.txt", new[] { 0 }));
            store.ToggleFavorite(R("late", "Late"));
        }
        using var again = new UsageStore(path);
        var fav = again.Favorites();
        Assert.Equal(new[] { "x.txt", "Late" }, fav.Select(f => f.Title).ToArray());
        Assert.Equal(ResultKind.File, fav[0].Kind);
        Assert.Equal("C:\\x.txt", fav[0].Payload);
        Assert.Equal("path:C:\\x.txt", fav[0].Icon);
        Assert.Null(fav[0].Highlight);          // highlights belong to one query only
        Assert.Equal(0, fav[0].Score);
        Assert.True(again.ToggleFavorite(R("third", "Third")));   // numbering continues after the reload
        Assert.Equal("Third", again.Favorites()[^1].Title);
    }

    [Theory]
    [InlineData(ResultKind.Calculator, false)]
    [InlineData(ResultKind.Web, false)]
    [InlineData(ResultKind.Clipboard, false)]
    [InlineData(ResultKind.App, true)]
    [InlineData(ResultKind.File, true)]
    [InlineData(ResultKind.Folder, true)]
    [InlineData(ResultKind.Setting, true)]
    [InlineData(ResultKind.Action, true)]
    [InlineData(ResultKind.Snippet, true)]
    [InlineData(ResultKind.Extension, true)]
    [InlineData(ResultKind.Command, true)]
    public void OnlyDurableResults_CanBePinned(ResultKind kind, bool expected)
    {
        using var store = new UsageStore(":memory:");
        var r = R("k", kind: kind);
        Assert.Equal(expected, UsageStore.CanFavorite(r));
        Assert.Equal(expected, store.ToggleFavorite(r));
        Assert.Equal(expected, store.IsFavorite("k"));
    }

    [Fact]
    public void ClearingTheHistory_KeepsFavorites()
    {
        using var store = new UsageStore(":memory:");
        store.ToggleFavorite(R("keep"));
        store.Record("q", R("used"));
        store.ClearHistory();
        Assert.Empty(store.Recent(5));
        Assert.True(store.IsFavorite("keep"));
    }

    [Fact]
    public async Task EmptyQuery_ShowsFavoritesFirst_ThenRecent_WithoutRepeats()
    {
        using var usage = new UsageStore(":memory:");
        var engine = new SearchEngine(new[] { new Fixed() }, usage);
        usage.Record("", R("r1", "Recent one"));
        usage.Record("", R("r2", "Recent two"));
        usage.Record("", R("r2", "Recent two"));
        usage.ToggleFavorite(R("f1", "Pinned one"));
        usage.ToggleFavorite(R("r1", "Recent one"));   // pinned AND used: must appear once, in the pinned group

        var results = await engine.SearchAsync("", 8, CancellationToken.None);
        Assert.Equal(new[] { "Pinned one", "Recent one", "Recent two" }, results.Select(r => r.Title).ToArray());
        Assert.Equal(new[] { true, true, false }, results.Select(r => r.Favorite).ToArray());
    }

    [Fact]
    public async Task EmptyQuery_ShowsAllFavorites_EvenBeyondTheLimit()
    {
        using var usage = new UsageStore(":memory:");
        var engine = new SearchEngine(new[] { new Fixed() }, usage);
        for (int i = 0; i < 12; i++) usage.ToggleFavorite(R($"f{i}", $"Fav {i}"));
        Assert.Equal(12, (await engine.SearchAsync("", 8, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Search_PutsFavoritesAhead_AndMarksThem()
    {
        using var usage = new UsageStore(":memory:");
        var engine = new SearchEngine(new[] { new Fixed(R("plain", "Alpha tool"), R("pinned", "Alpha tool two")) }, usage);
        var before = await engine.SearchAsync("alpha", 5, CancellationToken.None);
        Assert.DoesNotContain(before, r => r.Favorite);

        usage.ToggleFavorite(R("pinned", "Alpha tool two"));
        var after = await engine.SearchAsync("alpha", 5, CancellationToken.None);
        Assert.Equal("pinned", after[0].Key);
        Assert.True(after[0].Favorite);
        Assert.False(after[1].Favorite);
    }
}

public class ClipboardHistoryTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Add_StoresNewestFirst_AndDeduplicates()
    {
        using var store = new ClipboardHistoryStore(":memory:", ClipboardCipher.CreateEphemeral());
        int changes = 0;
        store.Changed += (_, _) => changes++;

        store.Add("primeiro", "Chrome", T0);
        store.Add("segundo", "Code", T0.AddMinutes(1));
        var again = store.Add("primeiro", "Firefox", T0.AddMinutes(2));

        Assert.Equal(new[] { "primeiro", "segundo" }, store.All.Select(e => e.Text).ToArray());
        Assert.Equal(2, again!.Count);
        Assert.Equal("Firefox", again.Source);           // the latest source wins
        Assert.Equal(T0, again.FirstUtc);
        Assert.Equal(T0.AddMinutes(2), again.LastUtc);
        Assert.Equal(3, changes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void BlankText_IsIgnored(string text)
    {
        using var store = new ClipboardHistoryStore(":memory:", ClipboardCipher.CreateEphemeral());
        Assert.Null(store.Add(text, "x"));
        Assert.Empty(store.All);
    }

    [Fact]
    public void HugeText_IsIgnored_ButTheLimitItselfIsKept()
    {
        using var store = new ClipboardHistoryStore(":memory:", ClipboardCipher.CreateEphemeral());
        Assert.Null(store.Add(new string('x', ClipboardHistoryStore.MaxTextLength + 1), "x"));
        Assert.NotNull(store.Add(new string('y', ClipboardHistoryStore.MaxTextLength), "x"));
    }

    [Fact]
    public void Text_IsStoredExactly_WithAccentsAndNewlines()
    {
        using var store = new ClipboardHistoryStore(":memory:", ClipboardCipher.CreateEphemeral());
        string text = "Olá,\r\n  ação  ✓\t fim ";
        Assert.Equal(text, store.Add(text, "x")!.Text);
        Assert.NotEqual(store.Add("ola", "x")!.Id, store.Add("Ola", "x")!.Id);   // case differences are different text
    }

    [Fact]
    public void CopiesWithinTheSameSecond_AreStillOrdered()
    {
        using var store = new ClipboardHistoryStore(":memory:", ClipboardCipher.CreateEphemeral());
        store.Add("a", "x", T0);
        store.Add("b", "x", T0.AddMilliseconds(300));
        store.Add("a", "x", T0.AddMilliseconds(600));   // copied again, still inside the first second
        Assert.Equal(new[] { "a", "b" }, store.All.Select(e => e.Text).ToArray());
        Assert.Equal(2, store.All[0].Count);
    }

    [Fact]
    public void Retention_DropsOldEntries_ButNotPinnedOnes()
    {
        using var store = new ClipboardHistoryStore(":memory:", ClipboardCipher.CreateEphemeral(), retentionDays: 7);
        var old = store.Add("velho", "x", T0)!;
        var oldPinned = store.Add("velho fixado", "x", T0)!;
        store.SetPinned(oldPinned.Id, true);
        store.Add("recente", "x", T0.AddDays(10));       // adding prunes relative to "now" = T0 + 10 days

        Assert.Null(store.Get(old.Id));
        Assert.NotNull(store.Get(oldPinned.Id));
        Assert.Contains(store.All, e => e.Text == "recente");
    }

    [Fact]
    public void MaxEntries_KeepsTheNewest_AndNeverDropsPinned()
    {
        using var store = new ClipboardHistoryStore(":memory:", ClipboardCipher.CreateEphemeral(), maxEntries: 3);
        var pinned = store.Add("fixado", "x", T0)!;
        store.SetPinned(pinned.Id, true);
        for (int i = 1; i <= 6; i++) store.Add($"item {i}", "x", T0.AddMinutes(i));

        var texts = store.All.Select(e => e.Text).ToList();
        Assert.Contains("fixado", texts);
        Assert.Equal(new[] { "item 6", "item 5", "item 4" }, texts.Where(t => t != "fixado").ToArray());
    }

    [Fact]
    public void Clear_KeepsPinnedByDefault()
    {
        using var store = new ClipboardHistoryStore(":memory:", ClipboardCipher.CreateEphemeral());
        store.SetPinned(store.Add("fica", "x")!.Id, true);
        store.Add("some", "x");
        store.Clear();
        Assert.Equal(new[] { "fica" }, store.All.Select(e => e.Text).ToArray());
        store.Clear(keepPinned: false);
        Assert.Empty(store.All);
    }

    [Fact]
    public void Delete_And_Persistence()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "c.db");
        long keep;
        using (var store = new ClipboardHistoryStore(path, ClipboardCipher.Open(Path.Combine(dir.Path, "k"), new FakeKeyProtector())))
        {
            keep = store.Add("guardado", "Code", T0)!.Id;
            store.SetPinned(keep, true);
            store.Delete(store.Add("apagado", "x", T0)!.Id);
        }
        using var again = new ClipboardHistoryStore(path, ClipboardCipher.Open(Path.Combine(dir.Path, "k"), new FakeKeyProtector()));
        var e = Assert.Single(again.All);
        Assert.Equal(keep, e.Id);
        Assert.True(e.Pinned);
        Assert.Equal("Code", e.Source);
    }

    // ---------------------------------------------------------------- provider

    private static ClipboardProvider Provider(ClipboardHistoryStore store, bool enabled = true) =>
        new(store, () => new AppSettings { ClipboardHistory = enabled }, () => T0.AddMinutes(30));

    private static Task<IReadOnlyList<SearchResult>> Ask(ClipboardProvider p, string q) =>
        p.QueryAsync(SearchContext.Create(q), CancellationToken.None);

    [Theory]
    [InlineData("clip", true)]
    [InlineData("CLIP", true)]
    [InlineData("clip algo", true)]
    [InlineData("clipboard", false)]
    [InlineData("clips", false)]
    [InlineData("xclip", false)]
    [InlineData("", false)]
    public async Task OnlyTheKeywordOpensTheHistory(string query, bool answers)
    {
        using var store = new ClipboardHistoryStore(":memory:", ClipboardCipher.CreateEphemeral());
        store.Add("algo", "x", T0);
        Assert.Equal(answers, (await Ask(Provider(store), query)).Count > 0);
    }

    [Fact]
    public async Task Disabled_OffersToTurnItOn_InsteadOfListing()
    {
        using var store = new ClipboardHistoryStore(":memory:", ClipboardCipher.CreateEphemeral());
        store.Add("segredo", "x", T0);
        var r = Assert.Single(await Ask(Provider(store, enabled: false), "clip"));
        Assert.Equal(ResultKind.Command, r.Kind);
        Assert.Equal(InternalCommands.ClipboardEnable, r.Payload);
        Assert.DoesNotContain("segredo", r.Title + r.Subtitle);
    }

    [Fact]
    public async Task BareKeyword_ListsNewestFirst_AndFiltering_IsFuzzy()
    {
        using var store = new ClipboardHistoryStore(":memory:", ClipboardCipher.CreateEphemeral());
        store.Add("senha do wifi", "Notepad", T0);
        store.Add("endereço: Rua das Flores, 10", "Chrome", T0.AddMinutes(5));
        store.Add("https://exemplo.com/relatorio", "Firefox", T0.AddMinutes(10));
        var p = Provider(store);

        var all = await Ask(p, "clip");
        Assert.Equal(new[] { "https://exemplo.com/relatorio", "endereço: Rua das Flores, 10", "senha do wifi" }, all.Select(r => r.Title).ToArray());
        Assert.All(all, r => Assert.Equal(ResultKind.Clipboard, r.Kind));

        var filtered = await Ask(p, "clip flores");
        Assert.Equal("endereço: Rua das Flores, 10", Assert.Single(filtered).Title);
        Assert.Empty(await Ask(p, "clip inexistente"));
    }

    [Fact]
    public async Task Results_DescribeTheEntry_AndMarkPinned()
    {
        using var store = new ClipboardHistoryStore(":memory:", ClipboardCipher.CreateEphemeral());
        store.Add("linha um\nlinha dois", "Code", T0);
        var e = store.Add("linha um\nlinha dois", "Code", T0.AddMinutes(1))!;   // copied twice
        store.SetPinned(e.Id, true);
        var r = Assert.Single(await Ask(Provider(store), "clip"));

        Assert.Equal("linha um", r.Title);                 // first line only
        Assert.Contains("há 29 min", r.Subtitle);
        Assert.Contains("Code", r.Subtitle);
        Assert.Contains("19 caracteres", r.Subtitle);
        Assert.Contains("copiado 2×", r.Subtitle);
        Assert.True(r.Favorite);
        Assert.Equal(e.Id.ToString(), r.Payload);
    }

    [Theory]
    [InlineData(10, "agora há pouco")]
    [InlineData(5 * 60, "há 5 min")]
    [InlineData(3 * 3600, "há 3 h")]
    [InlineData(30 * 3600, "ontem")]
    [InlineData(5 * 86400, "há 5 dias")]
    public void RelativeTimes(int seconds, string expected) =>
        Assert.Equal(expected, ClipboardProvider.Ago(TimeSpan.FromSeconds(seconds)));
}

public class QuicklinkTests
{
    private static AppSettings Settings(params WebSearchEngine[] extra)
    {
        var s = new AppSettings();
        s.SearchEngines.AddRange(extra);
        return s;
    }

    private static WebSearchEngine Link(string keyword, string name, string target) => new() { Keyword = keyword, Name = name, UrlTemplate = target };

    private static async Task<IReadOnlyList<SearchResult>> Ask(AppSettings s, string q) =>
        await new WebSearchProvider(() => s).QueryAsync(SearchContext.Create(q), CancellationToken.None);

    [Theory]
    [InlineData("https://x.com/?q={query}", true)]
    [InlineData("https://x.com/", false)]
    [InlineData(@"D:\Projetos", false)]
    public void SearchVersusPlainLink(string target, bool isSearch) =>
        Assert.Equal(isSearch, Quicklinks.IsSearch(Link("k", "n", target)));

    [Fact]
    public void Validation()
    {
        var others = new[] { Link("gh", "GitHub", "https://github.com/search?q={query}") };
        Assert.Null(Quicklinks.Validate(Link("docs", "Docs", "https://docs.example.com"), others));
        Assert.Contains("nome", Quicklinks.Validate(Link("a", " ", "x"), others));
        Assert.Contains("atalho", Quicklinks.Validate(Link("", "N", "x"), others));
        Assert.Contains("espaços", Quicklinks.Validate(Link("a b", "N", "x"), others));
        Assert.Contains("16", Quicklinks.Validate(Link(new string('a', 17), "N", "x"), others));
        Assert.Contains("destino", Quicklinks.Validate(Link("k", "N", "  "), others));
        Assert.Contains("já existe", Quicklinks.Validate(Link("GH", "Outro", "x"), others));
    }

    [Fact]
    public async Task APlainQuicklink_IsFoundByNameOrKeyword_AndOpensItsTarget()
    {
        var s = Settings(Link("proj", "Projetos de código", @"D:\Disco E - Backup\Programacao"));

        var byName = (await Ask(s, "projetos")).First(r => r.Key == "quicklink:proj");
        Assert.Equal(ResultKind.Url, byName.Kind);
        Assert.Equal(@"D:\Disco E - Backup\Programacao", byName.Payload);
        Assert.Equal("Projetos de código", byName.Title);

        var byKeyword = (await Ask(s, "proj")).First(r => r.Key == "quicklink:proj");
        Assert.True(byKeyword.Score > byName.Score - 100);   // an exact keyword is boosted
    }

    [Fact]
    public async Task SearchEntries_StillWorkByKeyword_AndPlainOnesAreNeverUsedAsSearches()
    {
        var s = Settings(Link("docs", "Documentação", "https://docs.example.com"));

        var yt = await Ask(s, "yt lofi");
        Assert.Equal("https://www.youtube.com/results?search_query=lofi", yt[0].Payload);

        // "docs algo" must not open the plain link with "algo" appended; it falls back to the default web search
        var results = await Ask(s, "docs algo");
        Assert.DoesNotContain(results, r => r.Payload.StartsWith("https://docs.example.com"));
        Assert.Contains(results, r => r.Kind == ResultKind.Web);
    }

    [Fact]
    public async Task TheFallbackSearch_IsNeverAPlainLink()
    {
        var s = Settings(Link("home", "Início", "https://inicio.example.com"));
        s.DefaultSearchEngine = "home";   // misconfigured: points at a plain link
        var web = (await Ask(s, "qualquer coisa")).Single(r => r.Kind == ResultKind.Web);
        Assert.StartsWith("https://", web.Payload);
        Assert.Contains("qualquer", web.Payload);
        Assert.DoesNotContain("inicio.example.com", web.Payload);
    }
}

public class OpenLauncherStepTests
{
    [Fact]
    public void TheStep_IsDescribed_Summarised_AndAlwaysValid()
    {
        var step = StepCatalog.NewStep(StepKind.OpenLauncher);
        Assert.Null(StepCatalog.Validate(step));
        Assert.Equal("Abrir o launcher", StepCatalog.Summarize(step));
        step.Args["query"] = "clip ";
        Assert.Equal("Abrir o launcher com “clip ”", StepCatalog.Summarize(step));
    }
}
