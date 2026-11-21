using Apparatus.Core.Actions;
using Apparatus.Core.Input;
using Apparatus.Core.Search;

namespace Apparatus.Core.Tests;

public class KeyComboTests
{
    [Theory]
    [InlineData("Ctrl+Shift+T", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x54u)]
    [InlineData("Enter", HotkeyModifiers.None, 0x0Du)]
    [InlineData("alt+left", HotkeyModifiers.Alt, 0x25u)]
    [InlineData("F5", HotkeyModifiers.None, 0x74u)]
    [InlineData("a", HotkeyModifiers.None, 0x41u)]
    public void Parses(string text, HotkeyModifiers mods, uint vk)
    {
        Assert.True(KeyCombo.TryParse(text, out var c));
        Assert.Equal(mods, c.Modifiers);
        Assert.Equal(vk, c.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Banana")]
    public void Rejects(string text) => Assert.False(KeyCombo.TryParse(text, out _));

    [Fact]
    public void Sequences_SplitOnSpaces()
    {
        Assert.True(KeyCombo.TryParseSequence("Ctrl+L  Ctrl+V   Enter", out var seq));
        Assert.Equal(3, seq.Count);
        Assert.Equal("Ctrl+L", seq[0].ToString());
        Assert.Equal(0x0Du, seq[2].VirtualKey);
        Assert.False(KeyCombo.TryParseSequence("Ctrl+L Nope", out _));
        Assert.False(KeyCombo.TryParseSequence("   ", out _));
    }
}

public class StepCatalogTests
{
    [Fact]
    public void EveryStepKind_HasADescriptor_WithUniqueKeys()
    {
        foreach (StepKind kind in Enum.GetValues<StepKind>())
        {
            var d = StepCatalog.Get(kind);
            Assert.Equal(kind, d.Kind);
            Assert.NotEmpty(d.Title);
            Assert.Equal(d.Fields.Count, d.Fields.Select(f => f.Key).Distinct().Count());
        }
        Assert.Equal(Enum.GetValues<StepKind>().Length, StepCatalog.All.Count);
    }

    [Fact]
    public void ChoiceFields_ListTheirChoices_AndDefaultsAreAmongThem()
    {
        foreach (var f in StepCatalog.All.SelectMany(d => d.Fields).Where(f => f.Type == FieldType.Choice))
        {
            Assert.NotNull(f.Choices);
            Assert.Contains(f.Choices!, c => c.Value == f.Default);
        }
    }

    [Fact]
    public void NewStep_CarriesDefaults()
    {
        var s = StepCatalog.NewStep(StepKind.RunPowerShell);
        Assert.Equal("true", s.Get("hidden"));
        Assert.True(s.GetBool("wait"));
        Assert.Equal("500", StepCatalog.NewStep(StepKind.Wait).Get("ms"));
    }

    [Theory]
    [InlineData(StepKind.OpenApp, "target", "", "Aplicativo ou arquivo")]
    [InlineData(StepKind.OpenUrl, "url", "isso nao e url", "http://")]
    [InlineData(StepKind.OpenUrl, "url", "ftp://servidor/x", "http://")]
    [InlineData(StepKind.SendKeys, "keys", "Ctrl+Banana", "não entendi")]
    [InlineData(StepKind.Wait, "ms", "abc", "número")]
    [InlineData(StepKind.Wait, "ms", "-5", "entre 0")]
    [InlineData(StepKind.Wait, "ms", "9999999", "entre 0")]
    [InlineData(StepKind.Window, "command", "explode", "opção inválida")]
    [InlineData(StepKind.Volume, "amount", "150", "entre 0 e 100")]
    public void Invalid_Steps_AreRejected_WithAReason(StepKind kind, string key, string value, string messagePart)
    {
        var step = StepCatalog.NewStep(kind);
        step.Args[key] = value;
        Assert.Contains(messagePart, StepCatalog.Validate(step));
    }

    [Theory]
    [InlineData(StepKind.OpenApp, "target", @"C:\Programas\app.exe")]
    [InlineData(StepKind.OpenUrl, "url", "https://exemplo.com/a?b=1")]
    [InlineData(StepKind.OpenUrl, "url", "mailto:eu@exemplo.com")]
    [InlineData(StepKind.SendKeys, "keys", "Ctrl+L Ctrl+V Enter")]
    [InlineData(StepKind.Wait, "ms", "0")]
    [InlineData(StepKind.RunPowerShell, "script", "Get-Date")]
    public void Valid_Steps_Pass(StepKind kind, string key, string value)
    {
        var step = StepCatalog.NewStep(kind);
        step.Args[key] = value;
        Assert.Null(StepCatalog.Validate(step));
    }

    [Fact]
    public void Defaults_AreValid_ForStepsWithoutRequiredText()
    {
        foreach (var kind in new[] { StepKind.Wait, StepKind.Window, StepKind.Media, StepKind.System })
            Assert.Null(StepCatalog.Validate(StepCatalog.NewStep(kind)));
        foreach (var kind in new[] { StepKind.Volume, StepKind.Brightness })
            Assert.Null(StepCatalog.Validate(StepCatalog.NewStep(kind)));
    }

    [Fact]
    public void ConditionWithoutValue_IsRejected()
    {
        var step = StepCatalog.NewStep(StepKind.Wait);
        step.When = new StepCondition { Kind = ConditionKind.ActiveProcessIs, Value = "  " };
        Assert.Contains("condição", StepCatalog.Validate(step));
    }

    [Theory]
    [InlineData(StepKind.Window, "command", "left-half", "Janela: Metade esquerda")]
    [InlineData(StepKind.Wait, "ms", "250", "Aguardar 250 ms")]
    [InlineData(StepKind.OpenUrl, "url", "https://a.com", "Abrir https://a.com")]
    [InlineData(StepKind.System, "command", "lock", "Bloquear o computador")]
    public void Summaries_ReadNaturally(StepKind kind, string key, string value, string expected)
    {
        var step = StepCatalog.NewStep(kind);
        step.Args[key] = value;
        Assert.Equal(expected, StepCatalog.Summarize(step));
    }

    [Fact]
    public void Summaries_MentionConditions_AndVolumeSigns()
    {
        var step = ActionStep.Create(StepKind.Volume, ("mode", "down"), ("amount", "7"));
        Assert.Equal("Volume −7%", StepCatalog.Summarize(step));
        step.When = new StepCondition { Kind = ConditionKind.ActiveProcessIs, Value = "chrome" };
        Assert.Contains("se o app ativo for chrome", StepCatalog.Summarize(step));
    }

    [Fact]
    public void LongPaths_AreShortened()
    {
        var step = ActionStep.Create(StepKind.OpenApp, ("target", @"C:\Program Files\Adobe\Adobe Photoshop 2025\Support Files\Photoshop.exe"));
        Assert.Equal(@"Abrir …\Photoshop.exe", StepCatalog.Summarize(step));
    }
}

public class HotkeyPlanTests
{
    private static HotkeyBinding B(long id, string keys, ScopeMode scope = ScopeMode.Everywhere, params string[] apps)
    {
        Assert.True(HotkeySpec.TryParse(keys, out var spec));
        return new HotkeyBinding(id, spec, scope, apps);
    }

    [Fact]
    public void ScopeRules_DecideWhereABindingApplies()
    {
        Assert.True(B(1, "Ctrl+Alt+1").AppliesTo("anything"));
        Assert.True(B(1, "Ctrl+Alt+1", ScopeMode.OnlyIn, "Photoshop").AppliesTo("photoshop.exe"));
        Assert.False(B(1, "Ctrl+Alt+1", ScopeMode.OnlyIn, "Photoshop").AppliesTo("chrome"));
        Assert.False(B(1, "Ctrl+Alt+1", ScopeMode.ExceptIn, "chrome").AppliesTo("Chrome"));
        Assert.True(B(1, "Ctrl+Alt+1", ScopeMode.ExceptIn, "chrome").AppliesTo("firefox"));
        Assert.False(B(1, "Ctrl+Alt+1", ScopeMode.OnlyIn, "Photoshop").AppliesTo(null));
    }

    [Fact]
    public void SameKeys_DifferentApps_PickTheMostSpecific()
    {
        var bindings = new[]
        {
            B(1, "Ctrl+Alt+T"),                                        // global
            B(2, "Ctrl+Alt+T", ScopeMode.OnlyIn, "terminal"),          // specific
            B(3, "Ctrl+Alt+T", ScopeMode.ExceptIn, "game"),            // in between
            B(4, "Ctrl+Alt+Y"),
        };
        Assert.Equal(new long[] { 2, 4 }, HotkeyPlan.ActiveFor(bindings, "terminal").Select(b => b.ActionId).OrderBy(x => x));
        Assert.Equal(new long[] { 3, 4 }, HotkeyPlan.ActiveFor(bindings, "notepad").Select(b => b.ActionId).OrderBy(x => x));
        Assert.Equal(new long[] { 1, 4 }, HotkeyPlan.ActiveFor(bindings, "game").Select(b => b.ActionId).OrderBy(x => x));
    }

    [Fact]
    public void ScopedOnlyBindings_AreInactiveElsewhere()
    {
        var bindings = new[] { B(1, "Ctrl+Alt+P", ScopeMode.OnlyIn, "photoshop") };
        Assert.Empty(HotkeyPlan.ActiveFor(bindings, "chrome"));
        Assert.Single(HotkeyPlan.ActiveFor(bindings, "Photoshop"));
    }

    [Fact]
    public void Conflicts_OnlyForSameKeys_SameSpecificity_AndOverlap()
    {
        var existing = new[]
        {
            B(1, "Ctrl+Alt+1"),
            B(2, "Ctrl+Alt+2", ScopeMode.OnlyIn, "a", "b"),
            B(3, "Ctrl+Alt+3", ScopeMode.ExceptIn, "a"),
        };
        // Two global bindings with the same keys fight.
        Assert.Equal(1, HotkeyPlan.FindConflict(B(9, "Ctrl+Alt+1"), existing)?.ActionId);
        // A global and a scoped one coexist: the scoped one wins in its app.
        Assert.Null(HotkeyPlan.FindConflict(B(9, "Ctrl+Alt+1", ScopeMode.OnlyIn, "a"), existing));
        // Scoped to disjoint apps: fine. Sharing an app: conflict.
        Assert.Null(HotkeyPlan.FindConflict(B(9, "Ctrl+Alt+2", ScopeMode.OnlyIn, "c"), existing));
        Assert.Equal(2, HotkeyPlan.FindConflict(B(9, "Ctrl+Alt+2", ScopeMode.OnlyIn, "b", "c"), existing)?.ActionId);
        // "Except in" lists share nearly every app: conflict.
        Assert.Equal(3, HotkeyPlan.FindConflict(B(9, "Ctrl+Alt+3", ScopeMode.ExceptIn, "z"), existing)?.ActionId);
        // Different keys never conflict; an action never conflicts with itself.
        Assert.Null(HotkeyPlan.FindConflict(B(9, "Ctrl+Alt+4"), existing));
        Assert.Null(HotkeyPlan.FindConflict(B(1, "Ctrl+Alt+1"), existing));
    }
}

public class ActionRunnerTests
{
    private sealed class Recorder : IStepExecutor
    {
        public List<string> Log { get; } = new();
        public Func<ActionStep, Exception?>? Fail { get; set; }
        public Func<Task>? Hold { get; set; }

        public bool CanExecute(StepKind kind) => kind != StepKind.Wait;

        public async Task ExecuteAsync(ActionStep step, CancellationToken ct)
        {
            Log.Add(StepCatalog.Summarize(step));
            if (Hold is not null) await Hold();
            if (Fail?.Invoke(step) is { } ex) throw ex;
        }
    }

    private sealed class FakeForeground : IForegroundProbe
    {
        public ForegroundInfo Current { get; set; } = new("notepad", "Sem título");
        public ForegroundInfo Get() => Current;
    }

    private static ActionDefinition Macro(params ActionStep[] steps) => new() { Id = 1, Name = "m", Steps = steps.ToList() };

    private static ActionStep Sys(string cmd) => ActionStep.Create(StepKind.System, ("command", cmd));

    private static (ActionRunner Runner, Recorder Rec, FakeForeground Fg, List<TimeSpan> Delays) Make()
    {
        var rec = new Recorder();
        var fg = new FakeForeground();
        var delays = new List<TimeSpan>();
        var runner = new ActionRunner(new[] { rec }, fg, (t, _) => { delays.Add(t); return Task.CompletedTask; });
        return (runner, rec, fg, delays);
    }

    [Fact]
    public async Task RunsStepsInOrder_AndHandlesWaitItself()
    {
        var (runner, rec, _, delays) = Make();
        var result = await runner.RunAsync(Macro(Sys("lock"), ActionStep.Create(StepKind.Wait, ("ms", "750")), Sys("sleep")));

        Assert.True(result!.Success);
        Assert.Equal(new[] { "Bloquear o computador", "Suspender" }, rec.Log);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(750) }, delays);
        Assert.All(result.Steps, s => Assert.Equal(StepOutcome.Done, s.Outcome));
    }

    [Fact]
    public async Task ConditionalSteps_AreSkippedUnlessTheForegroundMatches()
    {
        var (runner, rec, fg, _) = Make();
        var onlyChrome = Sys("lock");
        onlyChrome.When = new StepCondition { Kind = ConditionKind.ActiveProcessIs, Value = "chrome.exe" };
        var notChrome = Sys("sleep");
        notChrome.When = new StepCondition { Kind = ConditionKind.ActiveProcessIsNot, Value = "chrome" };
        var titled = Sys("monitor-off");
        titled.When = new StepCondition { Kind = ConditionKind.WindowTitleContains, Value = "relatório" };
        var notTitled = Sys("screensaver");
        notTitled.When = new StepCondition { Kind = ConditionKind.WindowTitleNotContains, Value = "relatório" };
        var action = Macro(onlyChrome, notChrome, titled, notTitled);

        fg.Current = new ForegroundInfo("notepad", "Meu Relatório final - Bloco de Notas");
        var result = await runner.RunAsync(action);
        Assert.Equal(new[] { StepOutcome.Skipped, StepOutcome.Done, StepOutcome.Done, StepOutcome.Skipped }, result!.Steps.Select(s => s.Outcome));

        rec.Log.Clear();
        fg.Current = new ForegroundInfo("Chrome", "Nova guia");
        await runner.RunAsync(action);
        Assert.Equal(new[] { "Bloquear o computador  (se o app ativo for chrome.exe)", "Iniciar a proteção de tela  (se o título não contiver “relatório”)" }, rec.Log);
    }

    [Fact]
    public async Task StopPolicy_EndsAtTheFirstFailure()
    {
        var (runner, rec, _, _) = Make();
        rec.Fail = s => s.Get("command") == "sleep" ? new InvalidOperationException("sem permissão") : null;
        var result = await runner.RunAsync(Macro(Sys("lock"), Sys("sleep"), Sys("screensaver")));

        Assert.False(result!.Success);
        Assert.Equal("sem permissão", result.FirstError);
        Assert.Equal(2, rec.Log.Count);   // the third step never ran
        Assert.Equal(new[] { StepOutcome.Done, StepOutcome.Failed }, result.Steps.Select(s => s.Outcome));
    }

    [Fact]
    public async Task ContinuePolicy_RunsTheRest_ButStillReportsFailure()
    {
        var (runner, rec, _, _) = Make();
        rec.Fail = s => s.Get("command") == "sleep" ? new InvalidOperationException("x") : null;
        var action = Macro(Sys("lock"), Sys("sleep"), Sys("screensaver"));
        action.OnError = ErrorPolicy.Continue;
        var result = await runner.RunAsync(action);

        Assert.False(result!.Success);
        Assert.Equal(3, rec.Log.Count);
        Assert.Equal(StepOutcome.Done, result.Steps[2].Outcome);
    }

    [Fact]
    public async Task UnknownStepKind_FailsCleanly()
    {
        var runner = new ActionRunner(Array.Empty<IStepExecutor>(), new FakeForeground());
        var result = await runner.RunAsync(Macro(Sys("lock")));
        Assert.False(result!.Success);
        Assert.Contains("executor", result.FirstError);
    }

    [Fact]
    public async Task Cancellation_StopsBeforeTheNextStep()
    {
        var (runner, rec, _, _) = Make();
        using var cts = new CancellationTokenSource();
        rec.Hold = () => { cts.Cancel(); return Task.CompletedTask; };
        var result = await runner.RunAsync(Macro(Sys("lock"), Sys("sleep")), cts.Token);

        Assert.True(result!.Cancelled);
        Assert.False(result.Success);
        Assert.Single(rec.Log);
    }

    [Fact]
    public async Task SameAction_CannotRunTwiceAtOnce()
    {
        var (runner, rec, _, _) = Make();
        var gate = new TaskCompletionSource();
        rec.Hold = () => gate.Task;
        var action = Macro(Sys("lock"));

        var first = runner.RunAsync(action);
        Assert.True(runner.IsRunning(action.Id));
        Assert.Null(await runner.RunAsync(action));   // refused while the first is in progress

        gate.SetResult();
        Assert.True((await first)!.Success);
        Assert.False(runner.IsRunning(action.Id));
        Assert.NotNull(await runner.RunAsync(action)); // free again afterwards
    }
}

public class ActionStoreTests
{
    private static ActionDefinition Sample(string name = "Abrir Photoshop", string hotkey = "", ScopeMode scope = ScopeMode.Everywhere, params string[] apps) => new()
    {
        Name = name,
        Hotkey = hotkey,
        Scope = scope,
        ScopeApps = apps.ToList(),
        Steps = { ActionStep.Create(StepKind.OpenApp, ("target", @"C:\Programas\Photoshop.exe")) },
    };

    [Fact]
    public void CreateEditDelete_RoundTrip()
    {
        using var store = new ActionStore(":memory:");
        int changes = 0;
        store.Changed += (_, _) => changes++;

        var saved = store.Save(Sample(hotkey: "ctrl + alt + p"));
        Assert.True(saved.Id > 0);
        Assert.Equal("Ctrl+Alt+P", saved.Hotkey);              // canonical form
        Assert.Equal(@"C:\Programas\Photoshop.exe", saved.Steps[0].Get("target"));

        saved.Name = "Photoshop";
        saved.Steps.Add(StepCatalog.NewStep(StepKind.Wait));
        var edited = store.Save(saved);
        Assert.Equal(2, edited.Steps.Count);
        Assert.True(edited.IsMacro);
        Assert.Single(store.All);

        store.Delete(saved.Id);
        Assert.Empty(store.All);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Persists_StepsConditionsAndScope_AcrossReopen()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "a.db");
        using (var store = new ActionStore(path))
        {
            var a = Sample("Macro", "Ctrl+Alt+M", ScopeMode.OnlyIn, "Photoshop", "chrome.exe");
            a.OnError = ErrorPolicy.Continue;
            a.Category = "Trabalho";
            var wait = StepCatalog.NewStep(StepKind.Wait);
            wait.When = new StepCondition { Kind = ConditionKind.WindowTitleContains, Value = "Projeto" };
            a.Steps.Add(wait);
            store.Save(a);
        }
        using var again = new ActionStore(path);
        var r = again.All.Single();
        Assert.Equal(ScopeMode.OnlyIn, r.Scope);
        Assert.Equal(new[] { "Photoshop", "chrome" }, r.ScopeApps.ToArray());   // ".exe" normalised away
        Assert.Equal(ErrorPolicy.Continue, r.OnError);
        Assert.Equal("Trabalho", r.Category);
        Assert.Equal(ConditionKind.WindowTitleContains, r.Steps[1].When!.Kind);
        Assert.Equal("Projeto", r.Steps[1].When!.Value);
        Assert.Equal(StepKind.Wait, r.Steps[1].Kind);
    }

    [Theory]
    [InlineData("", "Dê um nome")]
    [InlineData("  ", "Dê um nome")]
    public void Name_IsRequired(string name, string message)
    {
        using var store = new ActionStore(":memory:");
        var ex = Assert.Throws<ActionValidationException>(() => store.Save(Sample(name)));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void NeedsAtLeastOneValidStep()
    {
        using var store = new ActionStore(":memory:");
        Assert.Contains("ao menos um passo", Assert.Throws<ActionValidationException>(() => store.Save(new ActionDefinition { Name = "vazia" })).Message);

        var bad = Sample();
        bad.Steps.Add(ActionStep.Create(StepKind.Wait, ("ms", "abc")));
        var ex = Assert.Throws<ActionValidationException>(() => store.Save(bad));
        Assert.StartsWith("Passo 2:", ex.Message);   // macros say which step is wrong
    }

    [Fact]
    public void ScopedHotkey_NeedsAnApp()
    {
        using var store = new ActionStore(":memory:");
        var ex = Assert.Throws<ActionValidationException>(() => store.Save(Sample(hotkey: "Ctrl+Alt+P", scope: ScopeMode.OnlyIn)));
        Assert.Contains("aplicativo", ex.Message);
    }

    [Theory]
    [InlineData("P")]                 // no modifier
    [InlineData("Ctrl+Alt")]          // no key
    [InlineData("Ctrl+Banana")]
    public void Hotkey_MustBeValid(string hotkey)
    {
        using var store = new ActionStore(":memory:");
        Assert.Contains("Atalho inválido", Assert.Throws<ActionValidationException>(() => store.Save(Sample(hotkey: hotkey))).Message);
    }

    [Fact]
    public void Hotkey_CannotBeTheLaunchers()
    {
        using var store = new ActionStore(":memory:") { LauncherHotkey = "Alt+Space" };
        Assert.Contains("atalho do launcher", store.ValidateHotkey(Sample(hotkey: "alt+space")));
        Assert.Null(store.ValidateHotkey(Sample(hotkey: "Alt+Shift+Space")));
    }

    [Fact]
    public void Hotkey_Conflicts_AreReportedWithTheOtherActionsName()
    {
        using var store = new ActionStore(":memory:");
        store.Save(Sample("Primeira", "Ctrl+Alt+P"));

        var problem = store.ValidateHotkey(Sample("Segunda", "Ctrl+Alt+P"));
        Assert.Contains("Primeira", problem);
        Assert.Throws<ActionValidationException>(() => store.Save(Sample("Segunda", "Ctrl+Alt+P")));

        // Scoped to an app it coexists with the global one; two scoped to the same app do not.
        var scoped = store.Save(Sample("Só no Photoshop", "Ctrl+Alt+P", ScopeMode.OnlyIn, "photoshop"));
        Assert.Throws<ActionValidationException>(() => store.Save(Sample("Outra", "Ctrl+Alt+P", ScopeMode.OnlyIn, "Photoshop.exe")));
        Assert.Null(store.ValidateHotkey(Sample("Outra", "Ctrl+Alt+P", ScopeMode.OnlyIn, "illustrator")));

        // Editing an action does not conflict with itself.
        scoped.Name = "Renomeada";
        Assert.Equal("Renomeada", store.Save(scoped).Name);
    }

    [Fact]
    public void DisabledActions_DoNotHoldHotkeys()
    {
        using var store = new ActionStore(":memory:");
        var a = store.Save(Sample("Primeira", "Ctrl+Alt+P"));
        store.SetEnabled(a.Id, false);
        Assert.Empty(store.Bindings);
        Assert.Null(store.ValidateHotkey(Sample("Segunda", "Ctrl+Alt+P")));
        store.SetEnabled(a.Id, true);
        Assert.Single(store.Bindings);
    }

    [Fact]
    public void RecordRun_UpdatesStatistics_WithoutRaisingChanged()
    {
        using var store = new ActionStore(":memory:");
        var a = store.Save(Sample());
        bool raised = false;
        store.Changed += (_, _) => raised = true;
        store.RecordRun(a.Id);
        store.RecordRun(a.Id);
        Assert.Equal(2, store.Get(a.Id)!.RunCount);
        Assert.NotNull(store.Get(a.Id)!.LastRunUtc);
        Assert.False(raised);
    }

    [Fact]
    public void ExportImport_RoundTrips_AndDropsClashingHotkeys()
    {
        using var source = new ActionStore(":memory:");
        var macro = Sample("Macro", "Ctrl+Alt+M");
        var cond = StepCatalog.NewStep(StepKind.Wait);
        cond.When = new StepCondition { Kind = ConditionKind.ActiveProcessIs, Value = "code" };
        macro.Steps.Add(cond);
        source.Save(macro);
        source.Save(Sample("Outra", "Ctrl+Alt+O"));
        string json = source.ExportJson();

        using var target = new ActionStore(":memory:");
        target.Save(Sample("Ocupante", "Ctrl+Alt+O"));
        var result = target.ImportJson(json);

        Assert.Equal(2, result.Imported);
        Assert.Empty(result.Skipped);
        var imported = target.All.Single(a => a.Name == "Macro");
        Assert.Equal("Ctrl+Alt+M", imported.Hotkey);
        Assert.Equal("code", imported.Steps[1].When!.Value);
        Assert.Equal("", target.All.Single(a => a.Name == "Outra").Hotkey);   // clashed with "Ocupante": hotkey dropped, action kept

        var again = target.ImportJson(json);
        Assert.Equal(0, again.Imported);
        Assert.Equal(2, again.Updated);
    }

    [Fact]
    public void Import_RejectsGarbage_AndSkipsInvalidEntries()
    {
        using var store = new ActionStore(":memory:");
        Assert.Throws<ActionValidationException>(() => store.ImportJson("não é json"));
        Assert.Throws<ActionValidationException>(() => store.ImportJson("{}"));

        var result = store.ImportJson("""{"Version":1,"Actions":[{"Name":"Sem passos","Steps":[]}]}""");
        Assert.Equal(0, result.Imported);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void SeedExamples_RunOnce_AreValid_AndHaveNoHotkeys()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "a.db");
        using (var store = new ActionStore(path))
        {
            store.SeedExamplesOnce();
            Assert.True(store.All.Count >= 8);
            Assert.All(store.All, a => { Assert.Null(store.Validate(a)); Assert.Equal("", a.Hotkey); });
            Assert.Contains(store.All, a => a.IsMacro);
            foreach (var a in store.All.ToList()) store.Delete(a.Id);
        }
        using var again = new ActionStore(path);
        again.SeedExamplesOnce();
        Assert.Empty(again.All);
    }

    [Fact]
    public async Task Provider_FindsEnabledActions_ByNameAndCategory()
    {
        using var store = new ActionStore(":memory:");
        var a = Sample("Abrir Photoshop", "Ctrl+Alt+P");
        a.Category = "Design";
        a = store.Save(a);
        var hidden = store.Save(Sample("Abrir Illustrator"));
        store.SetEnabled(hidden.Id, false);
        var provider = new ActionsProvider(store);

        var byName = await provider.QueryAsync(SearchContext.Create("photoshop"), CancellationToken.None);
        var r = Assert.Single(byName);
        Assert.Equal(ResultKind.Action, r.Kind);
        Assert.Equal(a.Id.ToString(), r.Payload);
        Assert.Contains("Ctrl+Alt+P", r.Subtitle);
        Assert.Contains("Photoshop.exe", r.Subtitle);

        Assert.Single(await provider.QueryAsync(SearchContext.Create("design"), CancellationToken.None));
        Assert.Empty(await provider.QueryAsync(SearchContext.Create("illustrator"), CancellationToken.None));
    }
}
