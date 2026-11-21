using Apparatus.Core.Backup;

namespace Apparatus.Core.Tests;

public class SyncPolicyTests
{
    private static readonly DateTime T0 = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private static BackupManifest File(string machine, int minutes) => new() { Machine = machine, CreatedUtc = T0.AddMinutes(minutes) };

    [Fact]
    public void NoFile_WritesFreely() =>
        Assert.Equal(SyncDecision.Write, SyncPolicy.BeforeWrite(null, "A", DateTime.MinValue));

    [Fact]
    public void OwnFile_IsAlwaysReplaced() =>
        Assert.Equal(SyncDecision.Write, SyncPolicy.BeforeWrite(File("A", 10), "A", DateTime.MinValue));

    [Fact]
    public void MachineNamesCompareIgnoringCase() =>
        Assert.Equal(SyncDecision.Write, SyncPolicy.BeforeWrite(File("pc-casa", 10), "PC-CASA", DateTime.MinValue));

    [Fact]
    public void AnotherMachinesNewerUnseenFile_IsNotOverwritten() =>
        Assert.Equal(SyncDecision.HoldBackConflict, SyncPolicy.BeforeWrite(File("B", 10), "A", T0));

    [Fact]
    public void AnotherMachinesFile_AlreadySeen_CanBeReplaced() =>
        Assert.Equal(SyncDecision.Write, SyncPolicy.BeforeWrite(File("B", 10), "A", T0.AddMinutes(10)));

    [Fact]
    public void AFileOlderThanWhatWasSeen_CanBeReplaced() =>
        Assert.Equal(SyncDecision.Write, SyncPolicy.BeforeWrite(File("B", 5), "A", T0.AddMinutes(10)));

    [Fact]
    public void FirstEverRun_WithAnotherMachinesFile_HoldsBack() =>
        Assert.Equal(SyncDecision.HoldBackConflict, SyncPolicy.BeforeWrite(File("B", 1), "A", DateTime.MinValue));

    [Fact]
    public void UnseenFromElsewhere_IsWhatTheStartupCheckAsks()
    {
        Assert.True(SyncPolicy.IsUnseenFromElsewhere(File("B", 10), "A", T0));
        Assert.False(SyncPolicy.IsUnseenFromElsewhere(File("A", 10), "A", T0));
        Assert.False(SyncPolicy.IsUnseenFromElsewhere(File("B", 10), "A", T0.AddMinutes(10)));
    }
}
