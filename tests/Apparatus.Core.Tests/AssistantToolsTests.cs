using System.Text.Json;
using Apparatus.Core.Actions;
using Apparatus.Core.Assistant;
using Apparatus.Core.Snippets;

namespace Apparatus.Core.Tests;

public class AssistantToolsTests : IDisposable
{
    private readonly SettingsCatalog _catalog = SettingsCatalog.LoadEmbedded();
    private readonly ActionStore _actions = new(":memory:");
    private readonly SnippetStore _snippets = new(":memory:");

    public void Dispose()
    {
        _actions.Dispose();
        _snippets.Dispose();
    }

    private AssistantTools Tools(IReadOnlyList<ActionDefinition>? userActions = null) => new(_catalog, new AssistantToolOptions
    {
        UserActions = userActions ?? Array.Empty<ActionDefinition>(),
        ValidateAction = _actions.Validate,
        ValidateSnippetTrigger = t => _snippets.ValidateTrigger(t),
    });

    private static AssistantToolCall Call(string name, string json, string id = "call_1") => new(id, name, json);

    private SuggestionResult Run(string name, string json, IReadOnlyList<ActionDefinition>? userActions = null) =>
        Tools(userActions).Evaluate(Call(name, json));

    // ------------------------------------------------------------------------ the catalog

    [Fact]
    public void Catalog_HasTheWholeEmbeddedList_AndPrinters()
    {
        Assert.True(_catalog.Pages.Count >= 100);
        Assert.NotNull(_catalog.Find("ms-settings:printers"));
        Assert.Contains("ms-settings:printers — ", _catalog.ToPromptLines());
    }

    [Fact]
    public void ModernPages_ComeFromTheCatalog_AreRealMsSettingsPages_AndNeverPointAtThemselves()
    {
        var withModern = _catalog.Pages.Where(p => p.ModernUri is not null).ToList();
        Assert.True(withModern.Count >= 4);
        foreach (var p in withModern)
        {
            Assert.StartsWith("ms-settings:", p.ModernUri, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(_catalog.Find(p.ModernUri));
            Assert.NotEqual(p.Uri, p.ModernUri, StringComparer.OrdinalIgnoreCase);
            Assert.False(p.Uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase), $"{p.Uri} já é uma página moderna");
        }
        Assert.Equal("ms-settings:appsfeatures", _catalog.Find("appwiz.cpl")!.ModernUri);
        Assert.Null(_catalog.Find("devmgmt.msc")!.ModernUri);   // no modern equivalent: nothing is invented
    }

    [Fact]
    public void TheModernLink_IsNotPartOfThePrompt_SoTheModelNeverSeesIt()
    {
        string lines = _catalog.ToPromptLines();
        Assert.Contains("appwiz.cpl — ", lines);
        Assert.DoesNotContain("moderna", lines, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("modern", lines, StringComparison.OrdinalIgnoreCase);
        var line = lines.Split('\n').Single(l => l.StartsWith("appwiz.cpl — "));
        Assert.DoesNotContain("appsfeatures", line);
    }

    [Fact]
    public void ModernAlternative_OnlyForClassicToolsWithAModernPage()
    {
        var tools = Tools();
        SuggestedAction Offer(string uri) => Run("open_windows_setting", JsonSerializer.Serialize(new { uri })).Action!;

        var extra = tools.ModernAlternative(Offer("appwiz.cpl"))!;
        Assert.Equal("ms-settings:appsfeatures", extra.Payload);
        Assert.Equal(SuggestionKind.OpenWindowsSetting, extra.Kind);
        Assert.Equal(SuggestionRisk.Low, extra.Risk);
        Assert.Equal("call_1+modern", extra.CallId);
        Assert.StartsWith("Abrir: ", extra.Title);

        Assert.Null(tools.ModernAlternative(Offer("devmgmt.msc")));            // no modern page known
        Assert.Null(tools.ModernAlternative(Offer("ms-settings:appsfeatures"))); // already modern
        Assert.Null(tools.ModernAlternative(Run("open_apparatus_screen", """{"screen":"snippets"}""").Action!));
    }

    // ------------------------------------------------------------------------ open_windows_setting

    [Fact]
    public void Setting_FromTheCatalog_IsAccepted_WithTheCatalogsOwnSpelling()
    {
        var r = Run("open_windows_setting", """{"uri":"  MS-SETTINGS:PRINTERS "}""");
        Assert.True(r.Accepted);
        Assert.Equal(SuggestionKind.OpenWindowsSetting, r.Action!.Kind);
        Assert.Equal("ms-settings:printers", r.Action.Payload);
        Assert.Equal(SuggestionRisk.Low, r.Action.Risk);
        Assert.Equal("call_1", r.Action.CallId);
    }

    [Theory]
    [InlineData("ms-settings:made-up-page")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("https://evil.example.com")]
    [InlineData("cmd.exe /c calc")]
    [InlineData("ms-settings:printers && calc")]
    [InlineData("")]
    public void Setting_OutsideTheCatalog_IsRejected(string uri)
    {
        var r = Run("open_windows_setting", JsonSerializer.Serialize(new { uri }));
        Assert.False(r.Accepted);
        Assert.NotNull(r.Rejection);
    }

    [Fact]
    public void Everything_InTheCatalog_Passes_AndNothingElse()
    {
        var tools = Tools();
        foreach (var page in _catalog.Pages)
            Assert.True(tools.Evaluate(Call("open_windows_setting", JsonSerializer.Serialize(new { uri = page.Uri }))).Accepted, page.Uri);
    }

    // ------------------------------------------------------------------------ open_apparatus_screen

    [Theory]
    [InlineData("settings")]
    [InlineData("SNIPPETS")]
    [InlineData("actions")]
    [InlineData("extensions")]
    [InlineData("capture")]
    public void Screens_InTheClosedList_AreAccepted(string screen) =>
        Assert.True(Run("open_apparatus_screen", JsonSerializer.Serialize(new { screen })).Accepted);

    [Theory]
    [InlineData("quit")]
    [InlineData("ClearHistory")]
    [InlineData("clipboard-clear")]
    [InlineData("")]
    public void Screens_ThatDestroyOrQuit_AreRejected(string screen) =>
        Assert.False(Run("open_apparatus_screen", JsonSerializer.Serialize(new { screen })).Accepted);

    [Fact]
    public void ScreenSchema_ListsExactlyTheClosedList_SoTheModelCanOfferCapture()
    {
        var spec = Tools().Specs().Single(x => x.Name == "open_apparatus_screen");
        using var doc = JsonDocument.Parse(spec.ParametersSchema);
        var offered = doc.RootElement.GetProperty("properties").GetProperty("screen").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(AssistantTools.Screens.Select(x => x.Id), offered);
        Assert.Contains("capture", offered);
    }

    [Fact]
    public void CaptureScreen_BecomesAButton_ThatOpensItsOwnPage()
    {
        var result = Run("open_apparatus_screen", """{"screen":"capture"}""");
        Assert.True(result.Accepted);
        Assert.Equal(SuggestionKind.OpenApparatusScreen, result.Action!.Kind);
        Assert.Equal("capture", result.Action.Payload);
    }

    // ------------------------------------------------------------------------ open_launcher / open_url

    [Fact]
    public void Launcher_TextIsTrimmed_AndLimited()
    {
        Assert.Equal("clip", Run("open_launcher", """{"query":"  clip  "}""").Action!.Payload);
        Assert.False(Run("open_launcher", """{"query":"   "}""").Accepted);
        Assert.False(Run("open_launcher", JsonSerializer.Serialize(new { query = new string('a', 201) })).Accepted);
        Assert.False(Run("open_launcher", """{"query":"a\nb"}""").Accepted);
    }

    [Theory]
    [InlineData("https://example.com/page?q=1", true)]
    [InlineData("http://example.com", true)]
    [InlineData("mailto:someone@example.com", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("ms-msdt:/id PCWDiagnostic", false)]
    [InlineData("example.com", false)]
    [InlineData("data:text/html,<script>1</script>", false)]
    public void Url_OnlyWebAndMailto(string url, bool ok) =>
        Assert.Equal(ok, Run("open_url", JsonSerializer.Serialize(new { url })).Accepted);

    // ------------------------------------------------------------------------ run_my_action

    private static ActionDefinition MyAction(string name, bool enabled = true, long id = 7) => new()
    {
        Id = id, Name = name, Enabled = enabled,
        Steps = { ActionStep.Create(StepKind.Media, ("key", "play-pause")) },
    };

    [Fact]
    public void RunMyAction_FindsByName_IgnoringCase_AndIsMediumRisk()
    {
        var r = Run("run_my_action", """{"name":" pausar música "}""", new[] { MyAction("Pausar Música") });
        Assert.True(r.Accepted);
        Assert.Equal(7, r.Action!.ActionId);
        Assert.Equal(SuggestionRisk.Medium, r.Action.Risk);
    }

    [Fact]
    public void RunMyAction_UnknownDisabledOrAmbiguous_IsRejected()
    {
        Assert.False(Run("run_my_action", """{"name":"nope"}""", new[] { MyAction("A") }).Accepted);
        Assert.False(Run("run_my_action", """{"name":"A"}""", new[] { MyAction("A", enabled: false) }).Accepted);
        Assert.False(Run("run_my_action", """{"name":"A"}""", new[] { MyAction("A", id: 1), MyAction("a", id: 2) }).Accepted);
        Assert.False(Run("run_my_action", """{"name":"A"}""").Accepted);   // nothing shared
    }

    [Fact]
    public void RunMyAction_IsOnlyOffered_WhenThereAreEnabledActions()
    {
        Assert.DoesNotContain(Tools().Specs(), s => s.Name == "run_my_action");
        Assert.DoesNotContain(Tools(new[] { MyAction("A", enabled: false) }).Specs(), s => s.Name == "run_my_action");
        Assert.Contains(Tools(new[] { MyAction("A") }).Specs(), s => s.Name == "run_my_action");
    }

    // ------------------------------------------------------------------------ propose_action

    private const string LeftHalf = """{"name":"Janela à esquerda","hotkey":"Ctrl+Alt+Left","steps":[{"kind":"Window","command":"left-half"}]}""";

    [Fact]
    public void ProposeAction_Valid_BecomesAnUnsavedDraft()
    {
        var r = Run("propose_action", LeftHalf);
        Assert.True(r.Accepted, r.Rejection);
        var draft = r.Action!.ActionDraft!;
        Assert.Equal(0, draft.Id);                       // never saved by the assistant
        Assert.Equal("Janela à esquerda", draft.Name);
        Assert.Equal("Ctrl+Alt+Left", draft.Hotkey);
        Assert.Equal(StepKind.Window, draft.Steps.Single().Kind);
        Assert.Equal("left-half", draft.Steps[0].Get("command"));
        Assert.Equal(SuggestionRisk.Medium, r.Action.Risk);
        Assert.Empty(_actions.All);                      // and nothing reached the store
    }

    [Fact]
    public void ProposeAction_AMacro_KeepsTheStepOrder()
    {
        var r = Run("propose_action", """
            {"name":"Reunião","steps":[
              {"kind":"OpenUrl","url":"https://meet.example.com"},
              {"kind":"Wait","ms":"1500"},
              {"kind":"Volume","mode":"set","amount":"30"}]}
            """);
        Assert.True(r.Accepted, r.Rejection);
        Assert.Equal(new[] { StepKind.OpenUrl, StepKind.Wait, StepKind.Volume }, r.Action!.ActionDraft!.Steps.Select(s => s.Kind));
        Assert.Equal("1500", r.Action.ActionDraft.Steps[1].Get("ms"));
    }

    [Theory]
    [InlineData("RunPowerShell")]
    [InlineData("RunCommand")]
    [InlineData("TypeText")]
    [InlineData("InsertSnippet")]
    [InlineData("System")]
    [InlineData("Brightness")]
    [InlineData("Nonsense")]
    public void ProposeAction_ForbiddenOrUnknownStepKinds_AreRejected(string kind)
    {
        var json = JsonSerializer.Serialize(new { name = "X", steps = new[] { new { kind, script = "Remove-Item C:\\ -Recurse", command = "del /s /q C:\\", text = "hi", snippet = "s", mode = "lock" } } });
        Assert.False(Run("propose_action", json).Accepted);
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("powershell")]
    [InlineData("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\"")]
    [InlineData("wscript.exe")]
    [InlineData("mshta.exe")]
    [InlineData("rundll32.exe")]
    [InlineData("C:\\temp\\run.bat")]
    [InlineData("C:\\temp\\run.ps1")]
    [InlineData("setup.msi")]
    [InlineData("payload.js")]
    public void ProposeAction_OpenApp_CannotBeUsedAsACommandRunner(string target)
    {
        var json = JsonSerializer.Serialize(new { name = "X", steps = new[] { new { kind = "OpenApp", target, args = "/c whoami" } } });
        Assert.False(Run("propose_action", json).Accepted);
    }

    [Fact]
    public void ProposeAction_OpenApp_OrdinaryProgram_IsFine_AndNeverAsAdmin()
    {
        var r = Run("propose_action", """{"name":"Bloco","steps":[{"kind":"OpenApp","target":"notepad.exe","admin":"true"}]}""");
        Assert.True(r.Accepted, r.Rejection);
        Assert.NotEqual("true", r.Action!.ActionDraft!.Steps[0].Get("admin"));
    }

    [Fact]
    public void ProposeAction_OpenPath_RefusesScripts()
    {
        Assert.False(Run("propose_action", """{"name":"X","steps":[{"kind":"OpenPath","path":"C:\\Users\\me\\Downloads\\x.vbs"}]}""").Accepted);
        Assert.True(Run("propose_action", """{"name":"X","steps":[{"kind":"OpenPath","path":"C:\\Users\\me\\Documents"}]}""").Accepted);
    }

    [Fact]
    public void ProposeAction_FieldsOfOtherKinds_AreIgnored_NotStored()
    {
        var r = Run("propose_action", """{"name":"X","steps":[{"kind":"Window","command":"maximize","target":"evil.exe","script":"x","url":"https://x"}]}""");
        Assert.True(r.Accepted, r.Rejection);
        Assert.Equal(new[] { "command" }, r.Action!.ActionDraft!.Steps[0].Args.Keys);
    }

    [Fact]
    public void ProposeAction_UsesTheRealStepValidators()
    {
        Assert.False(Run("propose_action", """{"name":"X","steps":[{"kind":"Window","command":"explode"}]}""").Accepted);
        Assert.False(Run("propose_action", """{"name":"X","steps":[{"kind":"Wait","ms":"99999999"}]}""").Accepted);
        Assert.False(Run("propose_action", """{"name":"X","steps":[{"kind":"Volume","mode":"set","amount":"500"}]}""").Accepted);
        Assert.False(Run("propose_action", """{"name":"X","steps":[{"kind":"OpenUrl","url":"javascript:alert(1)"}]}""").Accepted);
        Assert.False(Run("propose_action", """{"name":"X","steps":[{"kind":"SendKeys","keys":"Banana+Z"}]}""").Accepted);
        Assert.False(Run("propose_action", """{"name":"X","steps":[]}""").Accepted);
        Assert.False(Run("propose_action", """{"name":"","steps":[{"kind":"Media","key":"next"}]}""").Accepted);
    }

    [Fact]
    public void ProposeAction_TooManySteps_IsRejected()
    {
        string steps = string.Join(",", Enumerable.Repeat("""{"kind":"Wait","ms":"10"}""", AssistantTools.MaxStepsInProposedAction + 1));
        Assert.False(Run("propose_action", "{\"name\":\"X\",\"steps\":[" + steps + "]}").Accepted);
    }

    [Fact]
    public void ProposeAction_ABadHotkey_IsDropped_NotFatal()
    {
        var r = Run("propose_action", """{"name":"X","hotkey":"banana","steps":[{"kind":"Media","key":"next"}]}""");
        Assert.True(r.Accepted, r.Rejection);
        Assert.Equal("", r.Action!.ActionDraft!.Hotkey);
    }

    [Fact]
    public void ProposeAction_AHotkeyTakenByAnotherAction_IsDropped()
    {
        var existing = MyAction("Existing", id: 0);
        existing.Hotkey = "Ctrl+Alt+Left";
        _actions.Save(existing);

        var r = Run("propose_action", LeftHalf);
        Assert.True(r.Accepted, r.Rejection);
        Assert.Equal("", r.Action!.ActionDraft!.Hotkey);
    }

    // ------------------------------------------------------------------------ propose_snippet

    [Fact]
    public void ProposeSnippet_Valid_IsAnUnsavedDraft()
    {
        var r = Run("propose_snippet", """{"name":"E-mail","trigger":";mail","text":"nome@exemplo.com"}""");
        Assert.True(r.Accepted, r.Rejection);
        var s = r.Action!.SnippetDraft!;
        Assert.Equal(0, s.Id);
        Assert.Equal(";mail", s.Trigger);
        Assert.True(s.AutoExpand);
        Assert.Equal("nome@exemplo.com", s.Content);
        Assert.Empty(_snippets.All);
    }

    [Fact]
    public void ProposeSnippet_WithoutATrigger_IsLauncherOnly()
    {
        var r = Run("propose_snippet", """{"text":"Atenciosamente,\nMaria"}""");
        Assert.True(r.Accepted, r.Rejection);
        Assert.Equal("", r.Action!.SnippetDraft!.Trigger);
        Assert.False(r.Action.SnippetDraft.AutoExpand);
        Assert.False(string.IsNullOrWhiteSpace(r.Action.SnippetDraft.Name));
    }

    [Fact]
    public void ProposeSnippet_ATriggerThatCollides_IsDropped_AndSaidSo()
    {
        _snippets.Save(new Snippet { Name = "Mail", Trigger = ";mail", Content = "x" });
        var r = Run("propose_snippet", """{"trigger":";mail2","text":"outro"}""");   // ";mail" is a prefix of it
        Assert.True(r.Accepted, r.Rejection);
        Assert.Equal("", r.Action!.SnippetDraft!.Trigger);
        Assert.Contains("gatilho", r.Action.Description);
    }

    [Fact]
    public void ProposeSnippet_NeedsText_AndLimitsItsSize()
    {
        Assert.False(Run("propose_snippet", """{"trigger":";x1","text":"  "}""").Accepted);
        Assert.False(Run("propose_snippet", JsonSerializer.Serialize(new { text = new string('a', 4001) })).Accepted);
    }

    // ------------------------------------------------------------------------ malformed input, in general

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"uri\"")]
    [InlineData("null")]
    [InlineData("{\"uri\":")]
    [InlineData("{\"uri\":[\"ms-settings:printers\"]}")]
    [InlineData("{\"uri\":null}")]
    [InlineData("{\"uri\":{\"x\":1}}")]
    public void MalformedArguments_NeverThrow_AndAreRejected(string json)
    {
        var r = Run("open_windows_setting", json);
        Assert.False(r.Accepted);
        Assert.False(string.IsNullOrEmpty(r.Rejection));
    }

    [Fact]
    public void UnknownToolName_IsRejected()
    {
        Assert.False(Run("delete_everything", "{}").Accepted);
        Assert.False(Run("", "{}").Accepted);
        Assert.False(Run("OPEN_WINDOWS_SETTING", """{"uri":"ms-settings:printers"}""").Accepted);   // names are exact
    }

    [Fact]
    public void HugeArguments_AreRejected_BeforeParsing()
    {
        var json = "{\"uri\":\"" + new string('a', AssistantTools.MaxArgumentChars) + "\"}";
        Assert.False(Run("open_windows_setting", json).Accepted);
    }

    [Fact]
    public void NumbersAndBooleans_CountAsText_BecauseModelsSendThem()
    {
        var r = Run("propose_action", """{"name":"X","steps":[{"kind":"Wait","ms":1500}]}""");
        Assert.True(r.Accepted, r.Rejection);
        Assert.Equal("1500", r.Action!.ActionDraft!.Steps[0].Get("ms"));
    }

    [Fact]
    public void Evaluate_Many_AcceptsAtMostThree_AndKeepsOrder()
    {
        var calls = Enumerable.Range(1, 5).Select(i => Call("open_launcher", $"{{\"query\":\"q{i}\"}}", $"c{i}")).ToList();
        var results = Tools().Evaluate(calls);
        Assert.Equal(5, results.Count);
        Assert.Equal(new[] { true, true, true, false, false }, results.Select(r => r.Accepted));
        Assert.Equal(new[] { "c1", "c2", "c3", "c4", "c5" }, results.Select(r => r.Call.Id));
    }

    // ------------------------------------------------------------------------ declarations

    [Fact]
    public void Specs_AreValidJsonSchemaObjects_WithUniqueNames()
    {
        var specs = Tools(new[] { MyAction("A") }).Specs();
        Assert.Equal(specs.Count, specs.Select(s => s.Name).Distinct().Count());
        foreach (var spec in specs)
        {
            using var doc = JsonDocument.Parse(spec.ParametersSchema);
            Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
            Assert.True(doc.RootElement.TryGetProperty("properties", out _), spec.Name);
            Assert.False(string.IsNullOrWhiteSpace(spec.Description));
        }
    }

    [Fact]
    public void Specs_DoNotOfferAnythingDestructive()
    {
        var names = Tools(new[] { MyAction("A") }).Specs().Select(s => s.Name).ToList();
        Assert.All(names, n => Assert.DoesNotContain("delete", n));
        Assert.DoesNotContain("run_powershell", names);
        Assert.DoesNotContain("run_command", names);

        var proposeAction = Tools().Specs().Single(s => s.Name == "propose_action").ParametersSchema;
        Assert.DoesNotContain("RunPowerShell", proposeAction);
        Assert.DoesNotContain("RunCommand", proposeAction);
        Assert.DoesNotContain("TypeText", proposeAction);
    }

    [Fact]
    public void ProposeActionSchema_UsesNoFreeFormObjects_WhichSomeProvidersRefuse()
    {
        var schema = Tools().Specs().Single(s => s.Name == "propose_action").ParametersSchema;
        Assert.DoesNotContain("additionalProperties", schema);
        using var doc = JsonDocument.Parse(schema);
        var item = doc.RootElement.GetProperty("properties").GetProperty("steps").GetProperty("items");
        foreach (var p in item.GetProperty("properties").EnumerateObject())
            Assert.True(p.Value.TryGetProperty("type", out _), p.Name);
    }
}
