using System.IO.Compression;
using System.Text;
using Apparatus.Core.Actions;
using Apparatus.Core.Backup;
using Apparatus.Core.Search;
using Apparatus.Core.Settings;
using Apparatus.Core.Snippets;
using Apparatus.Core.Start;

namespace Apparatus.Core.Tests;

/// <summary>One complete set of stores in a temp folder, standing for one computer.</summary>
public sealed class Machine : IDisposable
{
    private readonly TempFolder _dir = new();

    public Machine(string name)
    {
        Name = name;
        Settings = new SettingsStore(_dir.File("settings.json"));
        Snippets = new SnippetStore(_dir.File("snippets.db"));
        Actions = new ActionStore(_dir.File("actions.db"));
        Start = new StartLayoutStore(_dir.File("start.json"));
        Backup = new BackupService(Settings, Snippets, Actions, Start, name);
    }

    public string Name { get; }
    public SettingsStore Settings { get; }
    public SnippetStore Snippets { get; }
    public ActionStore Actions { get; }
    public StartLayoutStore Start { get; }
    public BackupService Backup { get; }

    public void Fill()
    {
        Snippets.Save(new Snippet { Name = "E-mail", Trigger = ";email", Content = "eu@exemplo.com" });
        Snippets.Save(new Snippet { Name = "Assinatura", Trigger = ";ass", Content = "Abraços,\nEu" });
        Actions.Save(new ActionDefinition
        {
            Name = "Abrir o site",
            Steps = { ActionStep.Create(StepKind.OpenUrl, ("url", "https://exemplo.com")) },
        });
        Start.Pin(new SearchResult("app:x", ResultKind.App, "Programa X", "", "x-id"));
        Start.AddSection("Trabalho");
        Start.AddQuick(new SearchResult("folder:d", ResultKind.Folder, "Docs", "", @"C:\docs"));
        var s = Settings.Current.Clone();
        s.MaxResults = 11;
        s.StartColumns = 7;
        s.StartTheme = "dark";
        s.DefaultSearchEngine = "yt";
        s.Hotkey = "Ctrl+Alt+K";
        s.RunAtStartup = true;
        s.IndexRoots = new() { @"D:\dados" };
        s.StartMenuEnabled = true;
        s.ClipboardHistory = true;
        s.EnabledExtensions = new() { "hello" };
        Settings.Update(s);
    }

    public byte[] Export(BackupParts parts = BackupParts.All)
    {
        using var ms = new MemoryStream();
        Backup.Export(ms, parts);
        return ms.ToArray();
    }

    public BackupImportResult Import(byte[] file, BackupParts parts = BackupParts.All)
    {
        using var ms = new MemoryStream(file);
        return Backup.Import(BackupBundle.Read(ms), parts);
    }

    public void Dispose()
    {
        Snippets.Dispose();
        Actions.Dispose();
        _dir.Dispose();
    }
}

public class BackupBundleTests
{
    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
        }
        return ms.ToArray();
    }

    private static BackupContent Read(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return BackupBundle.Read(ms);
    }

    [Fact]
    public void RoundTrip_KeepsEveryPart_AndNonAsciiText()
    {
        var content = new BackupContent(new BackupManifest { Machine = "PC-ação", CreatedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), Snippets = 3 },
            "{\"a\":\"ç\"}", "[1]", "[2]", "{\"b\":\"✓\"}");
        using var ms = new MemoryStream();
        BackupBundle.Write(ms, content);

        var back = Read(ms.ToArray());

        Assert.Equal("PC-ação", back.Manifest.Machine);
        Assert.Equal(3, back.Manifest.Snippets);
        Assert.Equal("{\"a\":\"ç\"}", back.SettingsJson);
        Assert.Equal("[1]", back.SnippetsJson);
        Assert.Equal("[2]", back.ActionsJson);
        Assert.Equal("{\"b\":\"✓\"}", back.StartJson);
    }

    [Fact]
    public void MissingParts_ReadAsNull()
    {
        var back = Read(Zip(("manifest.json", "{\"Version\":1,\"Machine\":\"x\"}"), ("snippets.json", "[]")));
        Assert.Equal("[]", back.SnippetsJson);
        Assert.Null(back.SettingsJson);
        Assert.Null(back.ActionsJson);
        Assert.Null(back.StartJson);
    }

    [Fact]
    public void NotAZip_IsRejectedWithAClearMessage()
    {
        var ex = Assert.Throws<BackupFormatException>(() => Read(Encoding.UTF8.GetBytes("isto não é um zip")));
        Assert.Contains("não é um backup", ex.Message);
    }

    [Fact]
    public void ZipWithoutManifest_IsRejected()
    {
        Assert.Throws<BackupFormatException>(() => Read(Zip(("other.txt", "x"))));
    }

    [Fact]
    public void BrokenManifest_IsRejected()
    {
        Assert.Throws<BackupFormatException>(() => Read(Zip(("manifest.json", "{ not json"))));
    }

    [Fact]
    public void NewerVersion_IsRejected_WithAdviceToUpdate()
    {
        var ex = Assert.Throws<BackupFormatException>(() => Read(Zip(("manifest.json", "{\"Version\":99}"))));
        Assert.Contains("mais nova", ex.Message);
    }

    [Fact]
    public void VersionZero_IsRejected()
    {
        Assert.Throws<BackupFormatException>(() => Read(Zip(("manifest.json", "{\"Version\":0}"))));
    }

    [Fact]
    public void UnknownEntries_AreIgnored_AndNothingIsExtracted()
    {
        // A name with path tricks is just a name here: entries are read into memory, never written to disk.
        var back = Read(Zip(("manifest.json", "{\"Version\":1}"), ("..\\..\\evil.dll", "boom"), ("snippets.json", "[]")));
        Assert.Equal("[]", back.SnippetsJson);
    }
}

public class BackupServiceTests
{
    [Fact]
    public void FullRoundTrip_ReproducesEverythingOnAnotherMachine()
    {
        using var home = new Machine("CASA");
        using var work = new Machine("TRABALHO");
        home.Fill();

        var result = work.Import(home.Export());

        Assert.Empty(result.Problems);
        Assert.Equal(new[] { "Assinatura", "E-mail" }, work.Snippets.All.Select(s => s.Name).Order());
        Assert.Equal("Abrir o site", Assert.Single(work.Actions.All).Name);
        var layout = work.Start.Snapshot();
        Assert.Equal("Programa X", layout.Sections.SelectMany(s => s.Items).Single().Title);
        Assert.Contains(layout.Sections, s => s.Name == "Trabalho");
        Assert.Equal("Docs", Assert.Single(layout.Quick).Title);
        Assert.True(layout.Seeded, "an imported layout must not be re-seeded with defaults");
        Assert.Equal(1, result.PinnedItems);
        Assert.Equal(11, work.Settings.Current.MaxResults);
        Assert.Equal(7, work.Settings.Current.StartColumns);
        Assert.Equal("dark", work.Settings.Current.StartTheme);
        Assert.Equal("yt", work.Settings.Current.DefaultSearchEngine);
    }

    [Fact]
    public void ThingsThatBelongToTheMachine_AreNeitherExportedNorOverwritten()
    {
        using var home = new Machine("CASA");
        using var work = new Machine("TRABALHO");
        home.Fill();
        var mine = work.Settings.Current.Clone();
        mine.Hotkey = "Alt+Space";
        mine.RunAtStartup = false;
        mine.IndexRoots = new() { @"E:\meus" };
        mine.StartMenuEnabled = false;
        mine.ClipboardHistory = false;
        mine.EnabledExtensions = new() { "outra" };
        work.Settings.Update(mine);

        work.Import(home.Export());

        var s = work.Settings.Current;
        Assert.Equal("Alt+Space", s.Hotkey);
        Assert.False(s.RunAtStartup);
        Assert.Equal(new[] { @"E:\meus" }, s.IndexRoots);
        Assert.False(s.StartMenuEnabled);
        Assert.False(s.ClipboardHistory);
        Assert.Equal(new[] { "outra" }, s.EnabledExtensions);
    }

    [Fact]
    public void TheFileItselfCarriesNoMachineSpecificSettings()
    {
        using var home = new Machine("CASA");
        home.Fill();
        using var ms = new MemoryStream(home.Export());
        var content = BackupBundle.Read(ms);

        var exported = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(content.SettingsJson!, SettingsStore.JsonOptions)!;
        Assert.False(exported.RunAtStartup);
        Assert.Null(exported.IndexRoots);
        Assert.Empty(exported.EnabledExtensions);
        Assert.Equal(11, exported.MaxResults);
    }

    [Fact]
    public void ManifestDescribesTheContent()
    {
        using var home = new Machine("CASA");
        home.Fill();
        using var ms = new MemoryStream(home.Export());
        var m = BackupBundle.Read(ms).Manifest;

        Assert.Equal("CASA", m.Machine);
        Assert.Equal(2, m.Snippets);
        Assert.Equal(1, m.Actions);
        Assert.Equal(1, m.PinnedItems);
        Assert.True((DateTime.UtcNow - m.CreatedUtc).TotalMinutes < 1);
    }

    [Fact]
    public void ImportingOnlySnippets_LeavesTheRestAlone()
    {
        using var home = new Machine("CASA");
        using var work = new Machine("TRABALHO");
        home.Fill();
        work.Start.Pin(new SearchResult("app:mine", ResultKind.App, "Meu", "", "m"));

        var result = work.Import(home.Export(), BackupParts.Snippets);

        Assert.Equal(2, work.Snippets.All.Count);
        Assert.Empty(work.Actions.All);
        Assert.Equal("Meu", work.Start.Snapshot().Sections.SelectMany(s => s.Items).Single().Title);
        Assert.False(result.SettingsApplied);
        Assert.Null(result.Actions);
        Assert.NotNull(result.Snippets);
    }

    [Fact]
    public void ExportingOnlySomeParts_WritesOnlyThose()
    {
        using var home = new Machine("CASA");
        home.Fill();
        using var ms = new MemoryStream(home.Export(BackupParts.Actions));
        var content = BackupBundle.Read(ms);
        Assert.NotNull(content.ActionsJson);
        Assert.Null(content.SnippetsJson);
        Assert.Null(content.SettingsJson);
        Assert.Null(content.StartJson);
    }

    [Fact]
    public void SnippetsAndActions_AreMergedByName_NotDuplicated()
    {
        using var home = new Machine("CASA");
        using var work = new Machine("TRABALHO");
        home.Fill();
        work.Import(home.Export());
        work.Import(home.Export());   // a second time

        Assert.Equal(2, work.Snippets.All.Count);
        Assert.Single(work.Actions.All);
    }

    [Fact]
    public void ACorruptPart_IsReported_AndTheOthersStillApply()
    {
        using var work = new Machine("TRABALHO");
        var content = new BackupContent(new BackupManifest(), "{ nope", "{ nope", "{ nope", "{ nope");
        var result = work.Backup.Import(content, BackupParts.All);

        Assert.Equal(4, result.Problems.Count);
        Assert.False(result.SettingsApplied);

        using var home = new Machine("CASA");
        home.Fill();
        using var ms = new MemoryStream(home.Export());
        var good = BackupBundle.Read(ms);
        var mixed = good with { SnippetsJson = "{ nope" };
        var again = work.Backup.Import(mixed, BackupParts.All);

        Assert.Single(again.Problems);
        Assert.Single(work.Actions.All);
        Assert.True(again.SettingsApplied);
    }

    [Fact]
    public void ImportedStartLayout_IsCleaned()
    {
        using var work = new Machine("TRABALHO");
        const string json = """
        {
          "Sections": [
            { "Id": "a", "Name": "  ", "Items": [ { "Key": "k1", "Title": "Um" }, { "Key": "", "Title": "sem chave" }, { "Key": "K1", "Title": "repetido" } ] },
            { "Id": "", "Name": "B", "Items": [ { "Key": "k1", "Title": "de novo" }, { "Key": "k2", "Title": "Dois" } ] }
          ],
          "Quick": [ { "Key": "q1" }, { "Key": "q1" }, { "Key": "" }, { "Key": "q2" }, { "Key": "q3" }, { "Key": "q4" }, { "Key": "q5" },
                     { "Key": "q6" }, { "Key": "q7" }, { "Key": "q8" }, { "Key": "q9" } ]
        }
        """;

        int pinned = work.Start.ImportJson(json);

        var l = work.Start.Snapshot();
        Assert.Equal(2, pinned);
        Assert.Equal("Sem nome", l.Sections[0].Name);
        Assert.Equal(new[] { "Um" }, l.Sections[0].Items.Select(i => i.Title));
        Assert.Equal(new[] { "Dois" }, l.Sections[1].Items.Select(i => i.Title));
        Assert.False(string.IsNullOrEmpty(l.Sections[1].Id));
        Assert.Equal(StartLayoutStore.MaxQuick, l.Quick.Count);
        Assert.Equal(l.Quick.Count, l.Quick.Select(q => q.Key).Distinct().Count());
        Assert.True(l.Seeded);
    }

    [Fact]
    public void ImportedStartLayout_SurvivesRestart()
    {
        using var dir = new TempFolder();
        var path = dir.File("start.json");
        new StartLayoutStore(path).ImportJson("{\"Sections\":[{\"Id\":\"a\",\"Name\":\"X\",\"Items\":[{\"Key\":\"k\",\"Title\":\"T\"}]}]}");
        Assert.Equal("T", new StartLayoutStore(path).Snapshot().Sections.Single().Items.Single().Title);
    }

    [Fact]
    public void StartImport_RaisesChanged()
    {
        using var dir = new TempFolder();
        var store = new StartLayoutStore(dir.File("start.json"));
        int fired = 0;
        store.Changed += (_, _) => fired++;
        store.ImportJson("{\"Sections\":[]}");
        Assert.Equal(1, fired);
    }
}
