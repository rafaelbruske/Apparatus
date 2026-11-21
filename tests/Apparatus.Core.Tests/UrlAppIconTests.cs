using Apparatus.Core.Search;
using Apparatus.Core.Search.Providers;
using Apparatus.Core.Start;
using Apparatus.Core.Storage;

namespace Apparatus.Core.Tests;

public class UrlAppIconTests
{
    private const string SteamId = "steam://rungameid/287390";

    private static AppEntry Steam(string name = "Metro Last Light Redux") => new(SteamId, name, @"shell:AppsFolder\" + SteamId);

    private static async Task<SearchResult> Find(AppsProvider provider, string query) =>
        (await provider.QueryAsync(SearchContext.Create(query), CancellationToken.None)).Single();

    private static string ShortcutWithIcon(string folder, string fileName, out string icon, string? iconLine = null)
    {
        icon = Path.Combine(folder, "game.ico");
        File.WriteAllText(icon, "not really an icon");
        string shortcut = Path.Combine(folder, fileName);
        File.WriteAllText(shortcut, "[InternetShortcut]\r\nIDList=\r\nIconIndex=0\r\nURL=" + SteamId + "\r\n" + (iconLine ?? "IconFile=" + icon) + "\r\n");
        return shortcut;
    }

    [Fact]
    public async Task UrlApp_UsesTheIconFileNamedByItsShortcut()
    {
        using var dir = new TempFolder();
        Directory.CreateDirectory(Path.Combine(dir.Path, "Steam"));
        ShortcutWithIcon(Path.Combine(dir.Path, "Steam"), "Metro Last Light Redux.url", out string icon);
        var provider = new AppsProvider(new[] { dir.Path });

        provider.SetApps(new[] { Steam() });

        var result = await Find(provider, "metro last");
        Assert.Equal("path:" + icon, result.Icon);   // the .ico itself: the shell shows a generic page for the .url
        Assert.Equal(@"shell:AppsFolder\" + SteamId, result.Payload);   // launching is unchanged
    }

    [Fact]
    public async Task UrlApp_WithoutAShortcut_ShowsTheGlyph_NotAGenericDocument()
    {
        using var dir = new TempFolder();
        var provider = new AppsProvider(new[] { dir.Path });
        provider.SetApps(new[] { Steam() });

        Assert.Null((await Find(provider, "metro")).Icon);
    }

    [Theory]
    [InlineData("IconFile=C:\\no\\such\\file.ico")]
    [InlineData("IconFile=")]
    [InlineData("Other=1")]
    public async Task UrlApp_WhoseShortcutHasNoUsableIcon_ShowsTheGlyph(string iconLine)
    {
        using var dir = new TempFolder();
        ShortcutWithIcon(dir.Path, "Metro Last Light Redux.url", out _, iconLine);
        var provider = new AppsProvider(new[] { dir.Path });
        provider.SetApps(new[] { Steam() });
        Assert.Null((await Find(provider, "metro")).Icon);
    }

    [Fact]
    public async Task ShortcutNameMatchIgnoresCase_AndQuotedPathsAreAccepted()
    {
        using var dir = new TempFolder();
        string icon = Path.Combine(dir.Path, "g.ico");
        File.WriteAllText(icon, "x");
        File.WriteAllText(Path.Combine(dir.Path, "METRO LAST LIGHT REDUX.url"), $"[InternetShortcut]\r\nIconFile=\"{icon}\"\r\n");
        var provider = new AppsProvider(new[] { dir.Path });
        provider.SetApps(new[] { Steam() });
        Assert.Equal("path:" + icon, (await Find(provider, "metro")).Icon);
    }

    [Fact]
    public async Task OrdinaryApps_KeepTheirShellIcon_AndTheFolderIsNotEvenScanned()
    {
        var provider = new AppsProvider(new[] { @"Z:\does\not\exist" });
        provider.SetApps(new[] { new AppEntry("chrome-id", "Google Chrome", @"C:\chrome.exe") });
        Assert.Equal(@"shell:C:\chrome.exe", (await Find(provider, "chrome")).Icon);
    }

    [Fact]
    public async Task AnIconSourceAlreadyKnown_IsNotReplaced()
    {
        using var dir = new TempFolder();
        ShortcutWithIcon(dir.Path, "Metro Last Light Redux.url", out _);
        var provider = new AppsProvider(new[] { dir.Path });
        provider.SetApps(new[] { Steam() with { IconSource = "path:C:\\mine.ico" } });
        Assert.Equal("path:C:\\mine.ico", (await Find(provider, "metro")).Icon);
    }

    [Fact]
    public void ShortcutIndex_FindsNestedFiles_AndSkipsMissingRoots()
    {
        using var dir = new TempFolder();
        Directory.CreateDirectory(Path.Combine(dir.Path, "a", "b"));
        File.WriteAllText(Path.Combine(dir.Path, "a", "b", "Jogo.url"), "x");
        File.WriteAllText(Path.Combine(dir.Path, "a", "Outro.lnk"), "x");

        var index = new UrlShortcutIndex(new[] { @"Z:\nope", dir.Path });

        Assert.Equal(1, index.Count);
        Assert.NotNull(index.Find("jogo"));
        Assert.Null(index.Find("Outro"));
    }

    [Fact]
    public void IsUrlApp_OnlyForUrlIds()
    {
        Assert.True(Steam().IsUrlApp);
        Assert.False(new AppEntry("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Calculadora", "x").IsUrlApp);
        Assert.False(new AppEntry(@"C:\Windows\notepad.exe", "Bloco de Notas", "x").IsUrlApp);
    }

    [Fact]
    public void IconKey_Rules()
    {
        Assert.Equal("shell:p", new AppEntry("i", "n", "p").IconKey);
        Assert.Equal("path:f", new AppEntry("i", "n", "p", "path:f").IconKey);
        Assert.Null(new AppEntry("i", "n", "p", "").IconKey);
    }

    // ------------------------------------------------ remembered icons get corrected

    [Fact]
    public void UsageStore_CorrectsTheIconOfHistoryAndFavorites_AndKeepsItAfterRestart()
    {
        using var dir = new TempFolder();
        string db = dir.File("u.db");
        var stale = new SearchResult("app:" + SteamId, ResultKind.App, "Metro", "Aplicativo", "p", 0, "shell:old");
        using (var store = new UsageStore(db))
        {
            store.Record("", stale);
            store.ToggleFavorite(stale);
            store.Record("", new SearchResult("app:other", ResultKind.App, "Outro", "", "o", 0, "shell:o"));

            int changed = store.UpdateIcons(new Dictionary<string, string?> { ["app:" + SteamId] = "path:new.url" });

            Assert.Equal(2, changed);   // one history row and one favorite
            Assert.Equal(0, store.UpdateIcons(new Dictionary<string, string?> { ["app:" + SteamId] = "path:new.url" }));
        }

        using var again = new UsageStore(db);
        Assert.Equal("path:new.url", again.Recent(10).Single(r => r.Key == "app:" + SteamId).Icon);
        Assert.Equal("path:new.url", again.Favorites().Single().Icon);
        Assert.Equal("shell:o", again.Recent(10).Single(r => r.Key == "app:other").Icon);
    }

    [Fact]
    public void UsageStore_CanClearAnIconToTheGlyph()
    {
        using var store = new UsageStore(":memory:");
        store.Record("", new SearchResult("app:x", ResultKind.App, "X", "", "p", 0, "shell:x"));
        store.UpdateIcons(new Dictionary<string, string?> { ["app:x"] = null });
        Assert.Null(store.Recent(5).Single().Icon);
    }

    [Fact]
    public void StartLayout_CorrectsPinnedAndQuickIcons()
    {
        using var dir = new TempFolder();
        string path = dir.File("start.json");
        var store = new StartLayoutStore(path);
        var r = new SearchResult("app:" + SteamId, ResultKind.App, "Metro", "", "p", 0, "shell:old");
        store.Pin(r);
        store.AddQuick(r);
        int fired = 0;
        store.Changed += (_, _) => fired++;

        int changed = store.UpdateIcons(new Dictionary<string, string?> { ["app:" + SteamId] = "path:new.url" });

        Assert.Equal(2, changed);
        Assert.Equal(1, fired);
        var layout = new StartLayoutStore(path).Snapshot();
        Assert.Equal("path:new.url", layout.Sections.Single().Items.Single().Icon);
        Assert.Equal("path:new.url", layout.Quick.Single().Icon);
        Assert.Equal(0, store.UpdateIcons(new Dictionary<string, string?> { ["app:" + SteamId] = "path:new.url" }));
        Assert.Equal(1, fired);
    }
}
