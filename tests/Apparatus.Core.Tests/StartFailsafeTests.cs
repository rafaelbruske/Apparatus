using Apparatus.Core.Start;

namespace Apparatus.Core.Tests;

public class UiHeartbeatTests
{
    [Fact]
    public void AliveWhileBeating_DeadAfterSilence()
    {
        long now = 1_000;
        var hb = new UiHeartbeat(() => now);
        Assert.True(hb.IsAlive());

        now += 1_900;
        Assert.True(hb.IsAlive());
        now += 200;
        Assert.False(hb.IsAlive());

        hb.Beat();
        Assert.True(hb.IsAlive());
    }

    [Fact]
    public void TheSilenceLimitIsConfigurable()
    {
        long now = 0;
        var hb = new UiHeartbeat(() => now);
        now = 500;
        Assert.False(hb.IsAlive(maxSilenceMs: 400));
        Assert.True(hb.IsAlive(maxSilenceMs: 500));
    }
}

public class StartFailsafeTests
{
    private static StartFailsafe Make(TempFolder dir, string build) => new(dir.File("failsafe.json"), build);

    private static void Verify(StartFailsafe f)
    {
        for (int i = 0; i < StartFailsafe.CleanUsesToVerify; i++) f.RecordCleanUse();
    }

    [Fact]
    public void FirstRun_IsInProbation_AndTwoCleanUsesVerifyTheBuild()
    {
        using var dir = new TempFolder();
        var f = Make(dir, "26300.1");
        Assert.True(f.InProbation);
        Assert.False(f.WindowsChangedSinceVerified, "a first run is not an update");

        f.RecordCleanUse();
        Assert.True(f.InProbation);
        f.RecordCleanUse();

        Assert.False(f.InProbation);
        Assert.Equal("26300.1", f.VerifiedBuild);
    }

    [Fact]
    public void InProbation_TheFirstProblemSwitchesTheReplacementOff()
    {
        using var dir = new TempFolder();
        var f = Make(dir, "26300.1");

        var verdict = f.RecordProblem(StartProblem.OfficialAlsoOpened);

        Assert.True(verdict.Trip);
        Assert.True(f.IsTripped);
        Assert.Contains("26300.1", verdict.Message);
        Assert.Equal("26300.1", f.TrippedAtBuild);
    }

    [Fact]
    public void AfterAWindowsUpdate_TheVerifiedBuildNoLongerCounts()
    {
        using var dir = new TempFolder();
        Verify(Make(dir, "26200.5"));

        var after = Make(dir, "26300.1");   // Windows updated overnight; the app restarted

        Assert.True(after.InProbation);
        Assert.True(after.WindowsChangedSinceVerified);
        Assert.True(after.RecordProblem(StartProblem.OfficialAlsoOpened).Trip);
    }

    [Fact]
    public void OnAVerifiedBuild_OneProblemIsTolerated_TwoInARowTrip()
    {
        using var dir = new TempFolder();
        var f = Make(dir, "26300.1");
        Verify(f);

        Assert.False(f.RecordProblem(StartProblem.OfficialAlsoOpened).Trip);
        Assert.False(f.IsTripped);
        Assert.True(f.RecordProblem(StartProblem.OfficialAlsoOpened).Trip);
        Assert.True(f.IsTripped);
    }

    [Fact]
    public void ACleanUseBetweenProblems_ResetsTheCount()
    {
        using var dir = new TempFolder();
        var f = Make(dir, "26300.1");
        Verify(f);

        f.RecordProblem(StartProblem.MenuDidNotAppear);
        f.RecordCleanUse();
        Assert.False(f.RecordProblem(StartProblem.MenuDidNotAppear).Trip);
    }

    [Fact]
    public void ATolerableProblem_RestartsTheCountOfCleanUsesNeededToVerify()
    {
        using var dir = new TempFolder();
        var f = Make(dir, "26300.1");
        Verify(f);                                   // verified
        var other = Make(dir, "26300.2");            // a later update
        other.RecordCleanUse();                      // one clean use...
        Assert.True(other.RecordProblem(StartProblem.MenuDidNotAppear).Trip);   // ...does not buy any tolerance
    }

    [Fact]
    public void TheTripSurvivesRestart_AndNamesTheReason()
    {
        using var dir = new TempFolder();
        Make(dir, "26300.1").RecordProblem(StartProblem.MenuDidNotAppear);

        var again = Make(dir, "26300.1");

        Assert.True(again.IsTripped);
        Assert.Contains("não apareceu", again.TrippedReason);
    }

    [Fact]
    public void Reset_ClearsTheTrip_AndMakesTheNextUsesScrutinisedAgain()
    {
        using var dir = new TempFolder();
        var f = Make(dir, "26300.1");
        Verify(f);
        f.RecordProblem(StartProblem.OfficialAlsoOpened);
        f.RecordProblem(StartProblem.OfficialAlsoOpened);
        Assert.True(f.IsTripped);

        f.Reset();

        Assert.False(f.IsTripped);
        Assert.True(f.InProbation, "the user must see it work again before it is trusted");
        Assert.True(f.RecordProblem(StartProblem.OfficialAlsoOpened).Trip);
    }

    [Fact]
    public void ADifferentReasonForEachProblem()
    {
        using var dir1 = new TempFolder();
        using var dir2 = new TempFolder();
        var a = Make(dir1, "1.1").RecordProblem(StartProblem.OfficialAlsoOpened).Message;
        var b = Make(dir2, "1.1").RecordProblem(StartProblem.MenuDidNotAppear).Message;
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ACorruptStateFile_StartsFresh()
    {
        using var dir = new TempFolder();
        File.WriteAllText(dir.File("failsafe.json"), "{ not json");
        var f = Make(dir, "1.1");
        Assert.False(f.IsTripped);
        Assert.True(f.InProbation);
    }

    [Fact]
    public void VerifiedBuild_IsRememberedAcrossRestarts()
    {
        using var dir = new TempFolder();
        Verify(Make(dir, "26300.1"));
        var again = Make(dir, "26300.1");
        Assert.False(again.InProbation);
        Assert.False(again.WindowsChangedSinceVerified);
    }
}
