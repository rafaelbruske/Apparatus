using Apparatus.Core.Search;
using Apparatus.Core.Search.Providers;
using Apparatus.Core.Start;

namespace Apparatus.Core.Tests;

public class StartSeedTests
{
    private static AppEntry A(string name) => new("id-" + name, name, "shell:" + name);

    [Fact]
    public void MostUsedComeFirst_ThenWellKnownInstalled()
    {
        var installed = new[] { A("Zeta"), A("Calculadora"), A("Microsoft Edge"), A("Obscuro") };
        var used = new[] { StartSeed.AppResult(A("Zeta")) };

        var pinned = StartSeed.PinnedApps(installed, used);

        Assert.Equal(new[] { "Zeta", "Microsoft Edge", "Calculadora" }, pinned.Select(p => p.Title));
    }

    [Fact]
    public void NoDuplicates_WhenAMostUsedAppIsAlsoWellKnown()
    {
        var edge = A("Microsoft Edge");
        var pinned = StartSeed.PinnedApps(new[] { edge }, new[] { StartSeed.AppResult(edge) });
        Assert.Single(pinned);
    }

    [Fact]
    public void RespectsTheLimit_AndIgnoresNonApps()
    {
        var installed = Enumerable.Range(0, 30).Select(i => A("App" + i)).ToList();
        var used = installed.Select(StartSeed.AppResult).Append(new SearchResult("f", ResultKind.File, "doc", "", "x")).ToList();

        var pinned = StartSeed.PinnedApps(installed, used, max: 5);

        Assert.Equal(5, pinned.Count);
        Assert.All(pinned, p => Assert.Equal(ResultKind.App, p.Kind));
    }

    [Fact]
    public void ScoresAndFlagsAreCleared()
    {
        var r = StartSeed.AppResult(A("X")) with { Score = 99, Favorite = true, Highlight = new[] { 0 } };
        var pinned = StartSeed.PinnedApps(Array.Empty<AppEntry>(), new[] { r });
        Assert.Equal(0, pinned[0].Score);
        Assert.False(pinned[0].Favorite);
        Assert.Null(pinned[0].Highlight);
    }

    [Fact]
    public void WellKnownMatchesEnglishNamesToo()
    {
        var pinned = StartSeed.PinnedApps(new[] { A("Calculator"), A("Notepad") }, Array.Empty<SearchResult>());
        Assert.Equal(2, pinned.Count);
    }

    [Fact]
    public void QuickLinks_SkipEmptyFolders_AndAllArePinnable()
    {
        var links = StartSeed.QuickLinks(@"C:\u\Documents", "", @"C:\u\Pictures");
        Assert.Equal(new[] { "Configurações", "Documentos", "Imagens" }, links.Select(l => l.Title));
        Assert.All(links, l => Assert.True(StartLayoutStore.CanPin(l)));
    }
}

public class StartPlacementTests
{
    private static readonly PixelRect Work = new(0, 0, 1920, 1040);   // 1080p with a 40 px taskbar at the bottom
    private static readonly (int, int) Menu = (640, 720);

    [Fact]
    public void BottomTaskbar_CenteredIcons_CentresTheMenuAboveIt()
    {
        var r = StartPlacement.Compute(Work, ScreenEdge.Bottom, "taskbar", iconsCentered: true, Menu, gap: 12);
        Assert.Equal(new PixelRect((1920 - 640) / 2, 1040 - 720 - 12, 640, 720), r);
    }

    [Fact]
    public void BottomTaskbar_LeftIcons_OpensAtTheLeft()
    {
        var r = StartPlacement.Compute(Work, ScreenEdge.Bottom, "taskbar", iconsCentered: false, Menu, gap: 12);
        Assert.Equal(12, r.X);
    }

    [Theory]
    [InlineData("center", false, 640)]
    [InlineData("left", true, 12)]
    public void ExplicitPosition_OverridesTheTaskbarAlignment(string position, bool centeredIcons, int expectedX)
    {
        var r = StartPlacement.Compute(Work, ScreenEdge.Bottom, position, centeredIcons, Menu, gap: 12);
        Assert.Equal(expectedX, r.X);
    }

    [Fact]
    public void TopTaskbar_OpensBelowIt()
    {
        var work = new PixelRect(0, 40, 1920, 1040);
        var r = StartPlacement.Compute(work, ScreenEdge.Top, "taskbar", true, Menu, 12);
        Assert.Equal(52, r.Y);
    }

    [Fact]
    public void SideTaskbars_OpenBesideThem()
    {
        var left = StartPlacement.Compute(new PixelRect(60, 0, 1860, 1080), ScreenEdge.Left, "taskbar", true, Menu, 12);
        Assert.Equal(72, left.X);
        var right = StartPlacement.Compute(new PixelRect(0, 0, 1860, 1080), ScreenEdge.Right, "taskbar", true, Menu, 12);
        Assert.Equal(1860 - 640 - 12, right.X);
    }

    [Fact]
    public void SecondMonitor_KeepsItsOwnOrigin()
    {
        var work = new PixelRect(1920, -200, 2560, 1400);
        var r = StartPlacement.Compute(work, ScreenEdge.Bottom, "center", true, Menu, 12);
        Assert.Equal(1920 + (2560 - 640) / 2, r.X);
        Assert.Equal(-200 + 1400 - 720 - 12, r.Y);
    }

    [Fact]
    public void SmallScreen_ShrinksTheMenuToFit()
    {
        var work = new PixelRect(0, 0, 500, 600);
        var r = StartPlacement.Compute(work, ScreenEdge.Bottom, "taskbar", true, Menu, 12);
        Assert.Equal(476, r.Width);
        Assert.Equal(576, r.Height);
        Assert.True(r.X >= 0 && r.Y >= 0 && r.Right <= 500 && r.Bottom <= 600);
    }
}
