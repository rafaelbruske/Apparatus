using Apparatus.Core.Search;
using Apparatus.Core.Start;

namespace Apparatus.Core.Tests;

public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apparatus-test-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

public class StartLayoutStoreTests
{
    private static SearchResult App(string name) => new($"app:{name}", ResultKind.App, name, "", $"aumid-{name}");

    [Fact]
    public void Pin_AddsToFirstSection_AndCreatesItWhenMissing()
    {
        using var dir = new TempFolder();
        var store = new StartLayoutStore(dir.File("start.json"));
        Assert.True(store.IsEmpty);

        Assert.True(store.Pin(App("A")));

        var layout = store.Snapshot();
        Assert.Single(layout.Sections);
        Assert.Equal(StartLayoutStore.DefaultSectionName, layout.Sections[0].Name);
        Assert.Equal("A", layout.Sections[0].Items.Single().Title);
        Assert.True(store.IsPinned("APP:a"), "keys are compared ignoring case");
    }

    [Fact]
    public void Pin_Twice_IsRefused_AcrossSections()
    {
        using var dir = new TempFolder();
        var store = new StartLayoutStore(dir.File("start.json"));
        store.Pin(App("A"));
        string other = store.AddSection("Outra");

        Assert.False(store.Pin(App("A"), other));
        Assert.Single(store.Snapshot().Sections.SelectMany(s => s.Items));
    }

    [Theory]
    [InlineData(ResultKind.Calculator)]
    [InlineData(ResultKind.Snippet)]
    [InlineData(ResultKind.Clipboard)]
    [InlineData(ResultKind.Web)]
    [InlineData(ResultKind.Extension)]
    public void Pin_RefusesTransientKinds(ResultKind kind)
    {
        using var dir = new TempFolder();
        var store = new StartLayoutStore(dir.File("start.json"));
        Assert.False(store.Pin(new SearchResult("k", kind, "T", "", "p")));
        Assert.True(store.IsEmpty);
    }

    [Fact]
    public void Unpin_RemovesFromWhateverSection()
    {
        using var dir = new TempFolder();
        var store = new StartLayoutStore(dir.File("start.json"));
        string second = store.AddSection("2");
        store.Pin(App("A"), second);

        Assert.True(store.Unpin("app:A"));
        Assert.False(store.Unpin("app:A"));
        Assert.True(store.IsEmpty);
    }

    [Fact]
    public void Move_ChangesSectionAndPosition()
    {
        using var dir = new TempFolder();
        var store = new StartLayoutStore(dir.File("start.json"));
        store.Pin(App("A"));
        store.Pin(App("B"));
        string second = store.AddSection("2");
        store.Pin(App("C"), second);

        Assert.True(store.Move("app:A", second, 0));

        var l = store.Snapshot();
        Assert.Equal(new[] { "B" }, l.Sections[0].Items.Select(i => i.Title));
        Assert.Equal(new[] { "A", "C" }, l.Sections[1].Items.Select(i => i.Title));
        Assert.False(store.Move("app:nope", second));
        Assert.False(store.Move("app:B", "no-such-section"));
    }

    [Fact]
    public void SetOrder_ReordersAndKeepsUnlistedAtTheEnd()
    {
        using var dir = new TempFolder();
        var store = new StartLayoutStore(dir.File("start.json"));
        foreach (var n in new[] { "A", "B", "C", "D" }) store.Pin(App(n));
        string id = store.Snapshot().Sections[0].Id;

        store.SetOrder(id, new[] { "app:C", "app:A", "app:ghost" });

        Assert.Equal(new[] { "C", "A", "B", "D" }, store.Snapshot().Sections[0].Items.Select(i => i.Title));
    }

    [Fact]
    public void RemoveSection_MovesItemsToNeighbour_AndKeepsTheLastOne()
    {
        using var dir = new TempFolder();
        var store = new StartLayoutStore(dir.File("start.json"));
        store.Pin(App("A"));
        string second = store.AddSection("2");
        store.Pin(App("B"), second);
        string first = store.Snapshot().Sections[0].Id;

        Assert.True(store.RemoveSection(second));
        Assert.Equal(new[] { "A", "B" }, store.Snapshot().Sections.Single().Items.Select(i => i.Title));
        Assert.False(store.RemoveSection(first), "the last section stays");
    }

    [Fact]
    public void RemoveFirstSection_ItemsGoToTheNext()
    {
        using var dir = new TempFolder();
        var store = new StartLayoutStore(dir.File("start.json"));
        store.Pin(App("A"));
        string second = store.AddSection("2");
        store.Pin(App("B"), second);

        Assert.True(store.RemoveSection(store.Snapshot().Sections[0].Id));

        var l = store.Snapshot();
        Assert.Equal(second, l.Sections.Single().Id);
        Assert.Equal(new[] { "B", "A" }, l.Sections[0].Items.Select(i => i.Title));
    }

    [Fact]
    public void MoveSection_SwapsAndIgnoresOutOfRange()
    {
        using var dir = new TempFolder();
        var store = new StartLayoutStore(dir.File("start.json"));
        store.Pin(App("A"));
        string b = store.AddSection("B");

        store.MoveSection(b, -1);
        Assert.Equal(new[] { "B", StartLayoutStore.DefaultSectionName }, store.Snapshot().Sections.Select(s => s.Name));

        store.MoveSection(b, -1);   // already first: nothing happens
        Assert.Equal("B", store.Snapshot().Sections[0].Name);
    }

    [Fact]
    public void RenameSection_TrimsAndLimitsLength_AndRefusesEmpty()
    {
        using var dir = new TempFolder();
        var store = new StartLayoutStore(dir.File("start.json"));
        string id = store.AddSection("x");

        store.RenameSection(id, "  Trabalho  ");
        Assert.Equal("Trabalho", store.Snapshot().Sections[0].Name);

        store.RenameSection(id, new string('z', 100));
        Assert.Equal(40, store.Snapshot().Sections[0].Name.Length);

        store.RenameSection(id, "   ");
        Assert.Equal("Sem nome", store.Snapshot().Sections[0].Name);
    }

    [Fact]
    public void Quick_HasALimitAndNoDuplicates()
    {
        using var dir = new TempFolder();
        var store = new StartLayoutStore(dir.File("start.json"));
        for (int i = 0; i < StartLayoutStore.MaxQuick; i++) Assert.True(store.AddQuick(App("q" + i)));

        Assert.False(store.AddQuick(App("one-too-many")));
        Assert.False(store.AddQuick(App("q0")));
        Assert.True(store.RemoveQuick("app:q0"));
        Assert.True(store.AddQuick(App("one-too-many")));
    }

    [Fact]
    public void SeedOnce_FillsOnlyTheFirstTime()
    {
        using var dir = new TempFolder();
        string path = dir.File("start.json");
        var store = new StartLayoutStore(path);

        store.SeedOnce(new[] { App("A"), App("B"), new SearchResult("calc", ResultKind.Calculator, "2+2", "", "4") }, new[] { App("Q") });
        Assert.True(store.Seeded);
        Assert.Equal(new[] { "A", "B" }, store.Snapshot().Sections[0].Items.Select(i => i.Title));
        Assert.Single(store.Snapshot().Quick);

        // The user empties the menu; a restart must not bring the defaults back.
        store.Unpin("app:A");
        store.Unpin("app:B");
        var reopened = new StartLayoutStore(path);
        reopened.SeedOnce(new[] { App("A") }, new[] { App("Z") });
        Assert.True(reopened.IsEmpty);
        Assert.Single(reopened.Snapshot().Quick);
    }

    [Fact]
    public void SurvivesRestart_WithAllFields()
    {
        using var dir = new TempFolder();
        string path = dir.File("start.json");
        var store = new StartLayoutStore(path);
        store.Pin(new SearchResult("file:x", ResultKind.File, "Relatório", @"D:\docs", @"D:\docs\r.pdf", 0, "path:D:\\docs\\r.pdf"));
        store.AddSection("Jogos");

        var l = new StartLayoutStore(path).Snapshot();
        var item = l.Sections[0].Items.Single();
        Assert.Equal(ResultKind.File, item.Kind);
        Assert.Equal(@"D:\docs\r.pdf", item.Payload);
        Assert.Equal("path:D:\\docs\\r.pdf", item.Icon);
        Assert.Equal("Jogos", l.Sections[1].Name);
        Assert.Equal("file:x", item.ToResult().Key);
    }

    [Fact]
    public void BrokenFile_StartsEmpty_AndKeepsACopy()
    {
        using var dir = new TempFolder();
        string path = dir.File("start.json");
        File.WriteAllText(path, "{ this is not json");

        var store = new StartLayoutStore(path);

        Assert.True(store.IsEmpty);
        Assert.True(File.Exists(path + ".broken"));
        Assert.True(store.Pin(App("A")));   // and it keeps working
    }

    [Fact]
    public void Snapshot_IsADeepCopy()
    {
        using var dir = new TempFolder();
        var store = new StartLayoutStore(dir.File("start.json"));
        store.Pin(App("A"));

        var snap = store.Snapshot();
        snap.Sections[0].Items.Clear();
        snap.Sections[0].Name = "mutated";

        Assert.Single(store.Snapshot().Sections[0].Items);
        Assert.NotEqual("mutated", store.Snapshot().Sections[0].Name);
    }

    [Fact]
    public void Changed_FiresOncePerMutation_NotOnRefusals()
    {
        using var dir = new TempFolder();
        var store = new StartLayoutStore(dir.File("start.json"));
        int fired = 0;
        store.Changed += (_, _) => fired++;

        store.Pin(App("A"));                  // 1
        store.Pin(App("A"));                  // refused
        store.Unpin("app:ghost");             // refused
        store.AddSection("2");                // 1 (not two)
        store.Unpin("app:A");                 // 1

        Assert.Equal(3, fired);
    }
}

public class FakeTweakRegistry : ITweakRegistry
{
    public Dictionary<(string Key, string Name), int> Values { get; } = new();
    public HashSet<string> Refused { get; } = new();
    public HashSet<string> Ignored { get; } = new();

    public int? ReadDword(string keyPath, string valueName) =>
        Values.TryGetValue((keyPath, valueName), out var v) ? v : null;

    public void WriteDword(string keyPath, string valueName, int value)
    {
        if (Refused.Contains(valueName)) throw new UnauthorizedAccessException("blocked");
        if (Ignored.Contains(valueName)) return;
        Values[(keyPath, valueName)] = value;
    }

    public void DeleteValue(string keyPath, string valueName) => Values.Remove((keyPath, valueName));
}

public class TweakServiceTests
{
    private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    [Fact]
    public void Current_FallsBackToTheDefault_WhenTheValueIsAbsent()
    {
        using var dir = new TempFolder();
        var svc = new TweakService(new FakeTweakRegistry(), dir.File("b.json"));
        Assert.Equal(1, svc.Current("taskbar-alignment"));
        Assert.Equal(0, svc.Current("clock-seconds"));
    }

    [Fact]
    public void Apply_WritesTheValue_AndReportsRestartNeed()
    {
        using var dir = new TempFolder();
        var reg = new FakeTweakRegistry();
        var svc = new TweakService(reg, dir.File("b.json"));

        var seconds = svc.Apply("clock-seconds", 1);
        var align = svc.Apply("taskbar-alignment", 0);

        Assert.True(seconds.Success);
        Assert.True(seconds.NeedsExplorerRestart);
        Assert.Equal(1, reg.Values[(Adv, "ShowSecondsInSystemClock")]);
        Assert.True(align.Success);
        Assert.False(align.NeedsExplorerRestart, "alignment applies live");
    }

    [Fact]
    public void Apply_RefusesValuesOutsideTheOptions()
    {
        using var dir = new TempFolder();
        var reg = new FakeTweakRegistry();
        var svc = new TweakService(reg, dir.File("b.json"));

        Assert.False(svc.Apply("taskbar-alignment", 7).Success);
        Assert.Empty(reg.Values);
        Assert.False(svc.HasBackup("taskbar-alignment"), "nothing was touched, nothing to back up");
    }

    [Fact]
    public void Restore_PutsBackAnExistingValue()
    {
        using var dir = new TempFolder();
        var reg = new FakeTweakRegistry();
        reg.Values[(Adv, "TaskbarAl")] = 0;
        var svc = new TweakService(reg, dir.File("b.json"));

        svc.Apply("taskbar-alignment", 1);
        Assert.True(svc.IsChanged("taskbar-alignment"));
        var r = svc.Restore("taskbar-alignment");

        Assert.True(r.Success);
        Assert.Equal(0, reg.Values[(Adv, "TaskbarAl")]);
        Assert.False(svc.HasBackup("taskbar-alignment"));
    }

    [Fact]
    public void Restore_DeletesAValueThatDidNotExist()
    {
        using var dir = new TempFolder();
        var reg = new FakeTweakRegistry();
        var svc = new TweakService(reg, dir.File("b.json"));

        svc.Apply("clock-seconds", 1);
        svc.Restore("clock-seconds");

        Assert.False(reg.Values.ContainsKey((Adv, "ShowSecondsInSystemClock")));
    }

    [Fact]
    public void SecondApply_DoesNotOverwriteTheTrueOriginal()
    {
        using var dir = new TempFolder();
        var reg = new FakeTweakRegistry();
        reg.Values[(Adv, "TaskbarSi")] = 1;
        var svc = new TweakService(reg, dir.File("b.json"));

        svc.Apply("taskbar-size", 0);
        svc.Apply("taskbar-size", 2);
        svc.Restore("taskbar-size");

        Assert.Equal(1, reg.Values[(Adv, "TaskbarSi")]);
    }

    [Fact]
    public void MirrorValues_AreWrittenAndRestoredIndependently()
    {
        using var dir = new TempFolder();
        var reg = new FakeTweakRegistry();
        reg.Values[(Adv, "TaskbarGlomLevel")] = 0;
        reg.Values[(Adv, "MMTaskbarGlomLevel")] = 1;   // the second monitor had its own, different choice
        var svc = new TweakService(reg, dir.File("b.json"));

        svc.Apply("combine-buttons", 2);
        Assert.Equal(2, reg.Values[(Adv, "TaskbarGlomLevel")]);
        Assert.Equal(2, reg.Values[(Adv, "MMTaskbarGlomLevel")]);

        svc.Restore("combine-buttons");
        Assert.Equal(0, reg.Values[(Adv, "TaskbarGlomLevel")]);
        Assert.Equal(1, reg.Values[(Adv, "MMTaskbarGlomLevel")]);
    }

    [Fact]
    public void RefusedByWindows_ReportsFailure_AndRestoreStillWorks()
    {
        using var dir = new TempFolder();
        var reg = new FakeTweakRegistry();
        reg.Values[(Adv, "TaskbarDa")] = 1;
        reg.Refused.Add("TaskbarDa");
        var svc = new TweakService(reg, dir.File("b.json"));

        var r = svc.Apply("widgets", 0);

        Assert.False(r.Success);
        Assert.Contains("recusou", r.Message);
        Assert.Equal(1, reg.Values[(Adv, "TaskbarDa")]);
        Assert.False(svc.IsChanged("widgets"));
    }

    [Fact]
    public void SilentlyIgnoredWrite_IsDetectedByReadingBack()
    {
        using var dir = new TempFolder();
        var reg = new FakeTweakRegistry();
        reg.Values[(Adv, "TaskbarDa")] = 1;
        reg.Ignored.Add("TaskbarDa");
        var svc = new TweakService(reg, dir.File("b.json"));

        var r = svc.Apply("widgets", 0);

        Assert.False(r.Success);
        Assert.Contains("não manteve", r.Message);
    }

    [Fact]
    public void Backup_SurvivesRestart()
    {
        using var dir = new TempFolder();
        var reg = new FakeTweakRegistry();
        reg.Values[(Adv, "TaskbarAl")] = 0;
        new TweakService(reg, dir.File("b.json")).Apply("taskbar-alignment", 1);

        var after = new TweakService(reg, dir.File("b.json"));   // the app was restarted
        Assert.True(after.HasBackup("taskbar-alignment"));
        after.Restore("taskbar-alignment");

        Assert.Equal(0, reg.Values[(Adv, "TaskbarAl")]);
    }

    [Fact]
    public void RestoreAll_PutsEverythingBack()
    {
        using var dir = new TempFolder();
        var reg = new FakeTweakRegistry();
        var svc = new TweakService(reg, dir.File("b.json"));
        svc.Apply("clock-seconds", 1);
        svc.Apply("task-view", 0);
        svc.Apply("taskbar-alignment", 0);

        Assert.Equal(3, svc.RestoreAll());
        Assert.Empty(reg.Values);
        Assert.Equal(0, svc.RestoreAll());
    }

    [Fact]
    public void Restore_OfSomethingNeverChanged_IsAHarmlessSuccess()
    {
        using var dir = new TempFolder();
        var reg = new FakeTweakRegistry();
        reg.Values[(Adv, "TaskbarAl")] = 0;
        var svc = new TweakService(reg, dir.File("b.json"));

        Assert.True(svc.Restore("taskbar-alignment").Success);
        Assert.Equal(0, reg.Values[(Adv, "TaskbarAl")]);
    }

    [Fact]
    public void Catalog_IsConsistent()
    {
        Assert.Equal(TweakCatalog.All.Count, TweakCatalog.All.Select(t => t.Id).Distinct().Count());
        foreach (var t in TweakCatalog.All)
        {
            Assert.Contains(t.Options, o => o.Value == t.DefaultValue);
            Assert.NotEmpty(t.Title);
            Assert.StartsWith("Software\\", t.KeyPath);
        }
        Assert.Throws<ArgumentException>(() => TweakCatalog.Get("nope"));
    }
}
