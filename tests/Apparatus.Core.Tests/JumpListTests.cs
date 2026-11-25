using Apparatus.Core.Start;

namespace Apparatus.Core.Tests;

public class JumpListTests
{
    // Names of real jump list files, from a Windows 11 machine: the AppUserModelID of the app and the file Windows made for it.
    [Theory]
    [InlineData("Chrome", "5d696d521de238c3")]
    [InlineData("MSEdge", "ccba5a5986c77e43")]
    [InlineData("308046B0AF4A39CB", "6824f4a902c78fbd")]
    public void HashOf_MatchesTheNamesWindowsGivesTheFiles(string appId, string expected) =>
        Assert.Equal(expected, JumpLists.HashOf(appId));

    [Fact]
    public void HashOf_IgnoresCase() => Assert.Equal(JumpLists.HashOf("MSEdge"), JumpLists.HashOf("msedge"));

    private static readonly byte[] Signature = { 0x4C, 0, 0, 0, 0x01, 0x14, 0x02, 0, 0, 0, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46 };
    private static readonly byte[] Footer = { 0xAB, 0xFB, 0xBF, 0xBA };

    private static byte[] Item(params byte[] payload) =>
        new byte[16].Concat(Signature).Concat(payload).ToArray();   // the class id of the entry, then the shortcut

    [Fact]
    public void TaskShortcuts_ReturnsOnlyTheFirstCategory()
    {
        byte[] file = new byte[20]
            .Concat(Item(1, 2, 3)).Concat(Item(4, 5)).Concat(Footer)                 // tasks
            .Concat(new byte[] { 0, 0, 0, 0, 5, 0 }).Concat(Item(9, 9)).Concat(Footer) // a titled category: not wanted
            .ToArray();

        var tasks = JumpLists.TaskShortcuts(file);

        Assert.Equal(2, tasks.Count);
        Assert.Equal(Signature.Concat(new byte[] { 1, 2, 3 }), tasks[0]);
        Assert.Equal(Signature.Concat(new byte[] { 4, 5 }), tasks[1]);
    }

    [Fact]
    public void TaskShortcuts_IsEmptyWithoutShortcutsOrFooter()
    {
        Assert.Empty(JumpLists.TaskShortcuts(new byte[] { 1, 2, 3 }));
        Assert.Empty(JumpLists.TaskShortcuts(new byte[20].Concat(Item(1)).ToArray()));   // no category end marker
        Assert.Empty(JumpLists.TaskShortcuts(new byte[20].Concat(Footer).ToArray()));
    }

    [Fact]
    public void HasTasks_ReadsTheFileOfTheApp()
    {
        string folder = Path.Combine(Path.GetTempPath(), "apparatus-jl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.False(JumpLists.HasTasks("MSEdge", folder));
            File.WriteAllBytes(JumpLists.FileFor("MSEdge", folder), new byte[20].Concat(Item(1)).Concat(Footer).ToArray());
            Assert.True(JumpLists.HasTasks("MSEdge", folder));
            Assert.False(JumpLists.HasTasks("Chrome", folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
