using Apparatus.Core.Indexing;
using Apparatus.Core.Search;
using Apparatus.Core.Search.Providers;
using Apparatus.Core.Settings;
using Apparatus.Core.Storage;

namespace Apparatus.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apparatus-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string Touch(string relative)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

public class UsageStoreTests
{
    private static SearchResult R(string key, string title = "T") =>
        new(key, ResultKind.App, title, "sub", "payload", 0, "shell:x");

    [Fact]
    public void FrequentlyUsed_GetsBigger_Boost()
    {
        using var store = new UsageStore(":memory:");
        store.Record("ch", R("app:chrome"));
        store.Record("ch", R("app:chrome"));
        store.Record("ch", R("app:chrome"));
        store.Record("ch", R("app:other"));

        Assert.True(store.Boost("app:chrome", "ch") > store.Boost("app:other", "ch"));
        Assert.Equal(0, store.Boost("app:never", "ch"));
    }

    [Fact]
    public void QuerySpecificBoost_AppliesToPrefixesOfThePickedQuery()
    {
        using var store = new UsageStore(":memory:");
        store.Record("chrome", R("app:chrome"));
        double forPrefix = store.Boost("app:chrome", "ch");
        double forUnrelated = store.Boost("app:chrome", "zz");
        Assert.True(forPrefix > forUnrelated);
    }

    [Fact]
    public void Boost_DecaysWithAge()
    {
        using var store = new UsageStore(":memory:");
        var longAgo = DateTime.UtcNow.AddDays(-365);
        store.Record("", R("app:old"), longAgo);
        store.Record("", R("app:new"));
        Assert.True(store.Boost("app:new", "") > store.Boost("app:old", ""));
    }

    [Fact]
    public void Recent_ListsMostRelevantFirst_AndSurvivesReopen()
    {
        using var dir = new TempDir();
        var db = Path.Combine(dir.Path, "u.db");
        using (var store = new UsageStore(db))
        {
            store.Record("a", R("k1", "Rarely"));
            store.Record("b", R("k2", "Often"));
            store.Record("b", R("k2", "Often"));
            store.Record("b", R("k2", "Often"));
        }
        using var reopened = new UsageStore(db);
        var recent = reopened.Recent(5);
        Assert.Equal(new[] { "Often", "Rarely" }, recent.Select(r => r.Title).ToArray());
        Assert.Equal("shell:x", recent[0].Icon);
    }

    [Fact]
    public void ClearHistory_RemovesEverything()
    {
        using var store = new UsageStore(":memory:");
        store.Record("a", R("k1"));
        store.ClearHistory();
        Assert.Empty(store.Recent(5));
        Assert.Equal(0, store.Boost("k1", "a"));
    }

    [Fact]
    public void AppsCache_RoundTrips()
    {
        using var store = new UsageStore(":memory:");
        store.SaveAppsCache(new[] { ("id1", "Notepad", "p1"), ("id2", "Paint", "p2") });
        store.SaveAppsCache(new[] { ("id3", "Calc", "p3") });
        var apps = store.LoadAppsCache();
        Assert.Single(apps);
        Assert.Equal("Calc", apps[0].Name);
    }
}

public class FileIndexTests
{
    private static IndexOptions Options(params string[] excluded) => new() { ExcludedNames = excluded };

    [Fact]
    public void Build_IndexesFilesAndFolders_WithCorrectPaths()
    {
        using var t = new TempDir();
        var file = t.Touch(@"Projetos\Apparatus\Relatório Final.docx");
        t.Touch(@"Projetos\notas.txt");

        var seg = IndexSegment.Build(t.Path, Options(), CancellationToken.None);
        var query = FuzzyQuery.Parse("relatorio");
        var index = new FileIndex(Path.Combine(t.Path, "_cache"));
        index.LoadSnapshots(Array.Empty<string>());

        var paths = Enumerable.Range(1, seg.Count - 1).Select(seg.GetPath).ToList();
        Assert.Contains(file, paths);
        Assert.Contains(Path.Combine(t.Path, "Projetos"), paths);
        Assert.Equal(1, Enumerable.Range(1, seg.Count - 1).Count(i => seg.IsDirectory(i) && seg.GetName(i) == "Apparatus"));
        Assert.NotEqual(FuzzyMatcher.NoMatch, FuzzyMatcher.Score(seg.NameSpan(paths.IndexOf(file) + 1), query));
    }

    [Fact]
    public void Build_SkipsExcludedDirectoriesAtAnyDepth()
    {
        using var t = new TempDir();
        t.Touch(@"app\src\main.cs");
        t.Touch(@"app\node_modules\pkg\index.js");
        t.Touch(@"NODE_MODULES\x.js");

        var seg = IndexSegment.Build(t.Path, Options("node_modules"), CancellationToken.None);
        var names = Enumerable.Range(1, seg.Count - 1).Select(i => seg.GetName(i)).ToList();
        Assert.Contains("main.cs", names);
        Assert.DoesNotContain("index.js", names);
        Assert.DoesNotContain("x.js", names);
        Assert.DoesNotContain("node_modules", names, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_RespectsMaxEntries()
    {
        using var t = new TempDir();
        for (int i = 0; i < 50; i++) t.Touch($"f{i}.txt");
        var seg = IndexSegment.Build(t.Path, new IndexOptions { MaxEntries = 10 }, CancellationToken.None);
        Assert.True(seg.Truncated);
        Assert.True(seg.Count <= 11);
    }

    [Fact]
    public void Snapshot_RoundTrips()
    {
        using var t = new TempDir();
        t.Touch(@"a\b\c.txt");
        var seg = IndexSegment.Build(t.Path, Options(), CancellationToken.None);
        var snap = Path.Combine(t.Path, "_snap.bin");
        seg.Save(snap);

        var loaded = IndexSegment.TryLoad(snap);
        Assert.NotNull(loaded);
        Assert.Equal(seg.Count, loaded!.Count);
        Assert.Equal(seg.Root, loaded.Root);
        for (int i = 0; i < seg.Count; i++) Assert.Equal(seg.GetPath(i), loaded.GetPath(i));
        Assert.Null(IndexSegment.TryLoad(Path.Combine(t.Path, "missing.bin")));
    }

    [Fact]
    public async Task Search_FindsFiles_DropsDeleted_AndSeesNewFiles()
    {
        using var t = new TempDir();
        var keep = t.Touch(@"docs\orçamento 2025.xlsx");
        var doomed = t.Touch(@"docs\orçamento antigo.xlsx");
        var settings = new AppSettings { IndexRoots = new() { t.Path } };

        using var index = new FileIndex(Path.Combine(t.Path, "_cache"));
        var roots = FileIndex.ResolveRoots(settings);
        await index.RebuildAsync(roots, settings);

        var q = FuzzyQuery.Parse("orcamento");
        var hits = index.Search(q, 10, CancellationToken.None);
        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Path == keep);

        File.Delete(doomed);
        hits = index.Search(q, 10, CancellationToken.None);
        Assert.Single(hits);
        Assert.Equal(keep, hits[0].Path);

        // New file picked up by the watcher before the next scan.
        var created = t.Touch(@"docs\orçamento novo.xlsx");
        for (int i = 0; i < 40 && index.Search(q, 10, CancellationToken.None).All(h => h.Path != created); i++)
            await Task.Delay(100);
        Assert.Contains(index.Search(q, 10, CancellationToken.None), h => h.Path == created);
    }

    [Fact]
    public async Task Snapshots_AreReusedAfterRestart()
    {
        using var t = new TempDir();
        t.Touch("alpha-report.txt");
        var settings = new AppSettings { IndexRoots = new() { t.Path } };
        var roots = FileIndex.ResolveRoots(settings);
        var cache = Path.Combine(t.Path, "_cache");

        using (var first = new FileIndex(cache)) await first.RebuildAsync(roots, settings);

        using var second = new FileIndex(cache);
        second.LoadSnapshots(roots);
        Assert.False(second.NeedsRescan(roots, TimeSpan.FromHours(1)));
        Assert.Single(second.Search(FuzzyQuery.Parse("alpha"), 5, CancellationToken.None));
    }

    [Fact]
    public void ResolveRoots_DropsNestedAndMissingRoots()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(Path.Combine(t.Path, "inner"));
        var settings = new AppSettings
        {
            IndexRoots = new() { t.Path, Path.Combine(t.Path, "inner"), Path.Combine(t.Path, "does-not-exist") },
        };
        var roots = FileIndex.ResolveRoots(settings);
        Assert.Single(roots);
        Assert.Equal(t.Path.TrimEnd('\\') + "\\", roots[0], ignoreCase: true);
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void RoundTrips_AndRaisesChanged()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "settings.json");
        var store = new SettingsStore(path);
        Assert.Equal("Ctrl+Alt+Space", store.Current.Hotkey);

        AppSettings? seen = null;
        store.Changed += (_, s) => seen = s;
        var next = store.Current.Clone();
        next.Hotkey = "Alt+Space";
        next.IndexRoots = new() { @"D:\" };
        store.Update(next);

        Assert.Equal("Alt+Space", seen?.Hotkey);
        var reloaded = new SettingsStore(path);
        Assert.Equal("Alt+Space", reloaded.Current.Hotkey);
        Assert.Equal(new[] { @"D:\" }, reloaded.Current.IndexRoots);
        Assert.Contains(reloaded.Current.SearchEngines, e => e.Keyword == "yt");
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "settings.json");
        File.WriteAllText(path, "{ this is not json");
        var store = new SettingsStore(path);
        Assert.Equal("Ctrl+Alt+Space", store.Current.Hotkey);
        Assert.True(File.Exists(path + ".broken"));
    }
}

public class SearchEngineTests
{
    private sealed class FakeCatalogProvider : ISearchProvider
    {
        public string Id => "fake";
        public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchContext c, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchResult>>(new[]
            {
                new SearchResult("app:a", ResultKind.App, "Alpha Browser", "Aplicativo", "a", 50),
                new SearchResult("app:b", ResultKind.App, "Alpha Browser Beta", "Aplicativo", "b", 50),
            });
    }

    private sealed class ThrowingProvider : ISearchProvider
    {
        public string Id => "boom";
        public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchContext c, CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    private static SearchEngine Engine(UsageStore usage, params ISearchProvider[] providers) => new(providers, usage);

    [Fact]
    public async Task History_ReordersResults_AndEmptyQueryShowsRecent()
    {
        using var usage = new UsageStore(":memory:");
        var engine = Engine(usage, new FakeCatalogProvider());

        var first = await engine.SearchAsync("alpha", 5, CancellationToken.None);
        Assert.Equal("app:a", first[0].Key);

        engine.RecordUse("alpha", first.First(r => r.Key == "app:b"));
        engine.RecordUse("alpha", first.First(r => r.Key == "app:b"));
        var second = await engine.SearchAsync("alpha", 5, CancellationToken.None);
        Assert.Equal("app:b", second[0].Key);

        var recent = await engine.SearchAsync("", 5, CancellationToken.None);
        Assert.Equal("app:b", recent[0].Key);
    }

    [Fact]
    public async Task FailingProvider_DoesNotBreakTheOthers()
    {
        using var usage = new UsageStore(":memory:");
        var engine = Engine(usage, new ThrowingProvider(), new FakeCatalogProvider());
        var results = await engine.SearchAsync("alpha", 5, CancellationToken.None);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Highlight_IsFilled_ForNameResults()
    {
        using var usage = new UsageStore(":memory:");
        var engine = Engine(usage, new FakeCatalogProvider());
        var results = await engine.SearchAsync("alpha", 5, CancellationToken.None);
        Assert.NotNull(results[0].Highlight);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, results[0].Highlight!.ToArray());
    }

    [Fact]
    public async Task RealProviders_Calculator_Web_Settings_Commands()
    {
        using var usage = new UsageStore(":memory:");
        var settings = new AppSettings();
        var engine = Engine(usage,
            new CalculatorProvider(), new WebSearchProvider(() => settings),
            new WindowsSettingsProvider(), new InternalCommandsProvider());

        var calc = await engine.SearchAsync("2+2*3", 8, CancellationToken.None);
        Assert.Equal(ResultKind.Calculator, calc[0].Kind);
        Assert.Equal("8", calc[0].Payload);

        var units = await engine.SearchAsync("10 km to mi", 8, CancellationToken.None);
        Assert.Equal(ResultKind.Calculator, units[0].Kind);
        Assert.StartsWith("6,2137", units[0].Payload);

        var yt = await engine.SearchAsync("yt lofi beats", 8, CancellationToken.None);
        Assert.Equal(ResultKind.Web, yt[0].Kind);
        Assert.Equal("https://www.youtube.com/results?search_query=lofi%20beats", yt[0].Payload);

        var url = await engine.SearchAsync("github.com/dotnet", 8, CancellationToken.None);
        Assert.Equal(ResultKind.Url, url[0].Kind);
        Assert.Equal("https://github.com/dotnet", url[0].Payload);

        var wifi = await engine.SearchAsync("wifi", 8, CancellationToken.None);
        Assert.Equal("ms-settings:network-wifi", wifi[0].Payload);

        var bright = await engine.SearchAsync("brilho", 8, CancellationToken.None);   // keyword, not in the title
        Assert.Equal("ms-settings:display", bright[0].Payload);

        var accents = await engine.SearchAsync("notificacoes", 8, CancellationToken.None);
        Assert.Equal("ms-settings:notifications", accents[0].Payload);

        var quit = await engine.SearchAsync("sair apparatus", 8, CancellationToken.None);
        Assert.Equal(InternalCommands.Quit, quit[0].Payload);

        var nothing = await engine.SearchAsync("qzxwv", 8, CancellationToken.None);
        Assert.Single(nothing);                         // only the web fallback
        Assert.Equal(ResultKind.Web, nothing[0].Kind);
    }

    [Fact]
    public async Task AppsProvider_FindsByInitialsAndAccents()
    {
        var apps = new AppsProvider();
        apps.SetApps(new[]
        {
            new AppEntry("1", "Google Chrome", "chrome"),
            new AppEntry("2", "Calculadora", "calc"),
            new AppEntry("3", "Configurações", "settings"),
            new AppEntry("1", "Google Chrome (dup)", "chrome2"),
        });
        using var usage = new UsageStore(":memory:");
        var engine = Engine(usage, apps);

        Assert.Equal("Google Chrome", (await engine.SearchAsync("gc", 5, CancellationToken.None))[0].Title);
        Assert.Equal("Configurações", (await engine.SearchAsync("configuracoes", 5, CancellationToken.None))[0].Title);
        Assert.Equal(3, apps.Count);
    }
}
