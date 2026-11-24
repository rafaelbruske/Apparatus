using System.ComponentModel;
using Apparatus.Core.Assistant;

namespace Apparatus.Core.Tests;

public class AssistantChatTests
{
    private readonly FakeAssistantClient _client = new();
    private readonly SettingsCatalog _catalog = SettingsCatalog.LoadEmbedded();

    private AssistantChat Chat(AssistantSession? session = null, Func<AssistantEngine>? factory = null)
    {
        session ??= new AssistantSession();
        factory ??= () => new AssistantEngine(_client, new SystemPromptBuilder(_catalog, "1.0", "26200"), new AssistantTools(_catalog), session, offerTools: false);
        return new AssistantChat(factory, session);
    }

    [Fact]
    public async Task AnAnswer_IsStreamedIntoOneEntry_AfterTheQuestion()
    {
        _client.Reply(new TextDelta("Olá, "), new TextDelta("mundo."));
        var chat = Chat();
        var changes = new List<string>();
        chat.Entries.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null) foreach (ChatEntry entry in e.NewItems) entry.PropertyChanged += (s, a) => { if (a.PropertyName == nameof(ChatEntry.Text)) changes.Add(((ChatEntry)s!).Text); };
        };

        var outcome = await chat.AskAsync("  oi  ");

        Assert.Equal(AskOutcome.Answered, outcome);
        Assert.Equal(new[] { ChatEntryRole.User, ChatEntryRole.Assistant }, chat.Entries.Select(e => e.Role));
        Assert.Equal("oi", chat.Entries[0].Text);
        Assert.Equal("Olá, mundo.", chat.Entries[1].Text);
        Assert.Equal(new[] { "Olá, ", "Olá, mundo." }, changes);   // the screen sees it grow, not appear at once
        Assert.False(chat.IsBusy);
    }

    [Fact]
    public void AnAnswerThatHasNotStarted_ShowsThinking_ButOtherEntriesNeverDo()
    {
        var answer = new ChatEntry(ChatEntryRole.Assistant);
        var changed = new List<string?>();
        answer.PropertyChanged += (_, a) => changed.Add(a.PropertyName);

        Assert.Equal("Pensando…", answer.Display);
        answer.Text = "Oi";
        Assert.Equal("Oi", answer.Display);
        Assert.Contains(nameof(ChatEntry.Display), changed);   // so the screen replaces "Pensando…" when the first words arrive

        Assert.Equal("", new ChatEntry(ChatEntryRole.User).Display);
        Assert.Equal("", new ChatEntry(ChatEntryRole.Notice).Display);
    }

    [Fact]
    public async Task BusyIsRaisedAndLowered_AroundTheAnswer()
    {
        _client.Reply(new TextDelta("x"));
        var chat = Chat();
        var seen = new List<bool>();
        chat.BusyChanged += (_, _) => seen.Add(chat.IsBusy);

        await chat.AskAsync("q");

        Assert.Equal(new[] { true, false }, seen);
    }

    [Fact]
    public async Task ABlankQuestion_IsIgnored()
    {
        var chat = Chat();
        Assert.Equal(AskOutcome.Ignored, await chat.AskAsync("   "));
        Assert.Empty(chat.Entries);
        Assert.Empty(_client.Requests);
    }

    [Fact]
    public async Task AQuestionWhileAnotherIsRunning_IsIgnored()
    {
        var gate = new TaskCompletionSource();
        _client.PauseAfterFirst = gate;
        _client.Reply(new TextDelta("a"), new TextDelta("b"));
        var chat = Chat();

        var first = chat.AskAsync("one");
        Assert.True(chat.IsBusy);
        Assert.Equal(AskOutcome.Ignored, await chat.AskAsync("two"));

        gate.SetResult();
        Assert.Equal(AskOutcome.Answered, await first);
        Assert.Equal(2, chat.Entries.Count);
    }

    [Fact]
    public async Task AProviderError_BecomesAnErrorLine_AndTheQuestionIsTakenBack()
    {
        _client.Fail(AssistantErrors.FromStatus(429, null));
        var chat = Chat();

        var outcome = await chat.AskAsync("pergunta");

        Assert.Equal(AskOutcome.Failed, outcome);
        var only = Assert.Single(chat.Entries);
        Assert.Equal(ChatEntryRole.Error, only.Role);
        Assert.Contains("Limite", only.Text);
        Assert.Empty(chat.Session.Messages);
        Assert.False(chat.IsBusy);
    }

    [Fact]
    public async Task AMissingKey_FromTheFactory_IsShownTheSameWay()
    {
        var chat = Chat(factory: () => throw AssistantErrors.MissingKey());

        Assert.Equal(AskOutcome.Failed, await chat.AskAsync("oi"));
        Assert.Equal(ChatEntryRole.Error, Assert.Single(chat.Entries).Role);
        Assert.Contains("chave", chat.Entries[0].Text);
    }

    [Fact]
    public async Task AnUnexpectedException_NeverEscapes_AndNeverLeaksItsText()
    {
        var chat = Chat(factory: () => throw new InvalidOperationException("secret detail AIza-123"));

        Assert.Equal(AskOutcome.Failed, await chat.AskAsync("oi"));
        var error = Assert.Single(chat.Entries);
        Assert.DoesNotContain("AIza", error.Text);
        Assert.False(chat.IsBusy);
    }

    [Fact]
    public async Task TheErrorLine_DisappearsOnTheNextQuestion()
    {
        _client.Fail(AssistantErrors.FromStatus(503, null));
        _client.Reply(new TextDelta("agora foi"));
        var chat = Chat();

        await chat.AskAsync("um");
        await chat.AskAsync("dois");

        Assert.Equal(new[] { ChatEntryRole.User, ChatEntryRole.Assistant }, chat.Entries.Select(e => e.Role));
    }

    [Fact]
    public async Task Cancelling_LeavesNothingBehind()
    {
        var gate = new TaskCompletionSource();
        _client.PauseAfterFirst = gate;
        _client.Reply(new TextDelta("começo"), new TextDelta("fim"));
        var chat = Chat();

        var pending = chat.AskAsync("pergunta");
        while (chat.Entries.Count < 2 || chat.Entries[1].Text.Length == 0) await Task.Delay(5);   // the answer has started
        chat.CancelCurrent();
        var outcome = await pending;

        Assert.Equal(AskOutcome.Cancelled, outcome);
        Assert.Empty(chat.Entries);
        Assert.Empty(chat.Session.Messages);
        Assert.False(chat.IsBusy);
    }

    [Fact]
    public async Task Cancelling_WithAnOuterToken_Works_AndEarlierTurnsSurvive()
    {
        _client.Reply(new TextDelta("primeira"));
        var chat = Chat();
        await chat.AskAsync("um");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _client.Reply(new TextDelta("nunca"));
        Assert.Equal(AskOutcome.Cancelled, await chat.AskAsync("dois", cts.Token));

        Assert.Equal(new[] { "um", "primeira" }, chat.Entries.Select(e => e.Text));
    }

    [Fact]
    public async Task AnEmptyAnswer_LeavesTheEngineNoticeOnly_AndCountsAsFailed()
    {
        _client.Reply();
        var chat = Chat();

        Assert.Equal(AskOutcome.Failed, await chat.AskAsync("q"));
        var notice = Assert.Single(chat.Entries);
        Assert.Equal(ChatEntryRole.Notice, notice.Role);
    }

    [Fact]
    public async Task SecondQuestion_SendsTheFirstAsHistory()
    {
        _client.Reply(new TextDelta("r1"));
        _client.Reply(new TextDelta("r2"));
        var chat = Chat();

        await chat.AskAsync("p1");
        await chat.AskAsync("p2");

        Assert.Equal(new[] { "p1", "r1", "p2" }, _client.Requests[1].Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task Reset_ForgetsEverything()
    {
        _client.Reply(new TextDelta("r"));
        var chat = Chat();
        await chat.AskAsync("p");

        chat.Reset();

        Assert.Empty(chat.Entries);
        Assert.Empty(chat.Session.Messages);
    }

    [Fact]
    public async Task AnIdleConversation_ExpiresOnNextUse_ButNotBefore()
    {
        var now = DateTime.UtcNow;
        var session = new AssistantSession(idleTimeout: TimeSpan.FromMinutes(15), clock: () => now);
        _client.Reply(new TextDelta("r1"));
        _client.Reply(new TextDelta("r2"));
        var chat = Chat(session);

        await chat.AskAsync("p1");
        now = now.AddMinutes(14);
        chat.ExpireIfIdle();
        Assert.Equal(2, chat.Entries.Count);

        now = now.AddMinutes(2);
        chat.ExpireIfIdle();
        Assert.Empty(chat.Entries);

        await chat.AskAsync("p2");   // and a question after it starts from scratch
        Assert.Equal(new[] { "p2" }, _client.Requests[1].Messages.Select(m => m.Text));
    }

    [Fact]
    public void TheDefaultIdleTimeout_IsFifteenMinutes()
    {
        var now = DateTime.UtcNow;
        var s = new AssistantSession(clock: () => now);
        s.AddUser("x");
        now = now.AddMinutes(14);
        Assert.False(s.IsExpired);
        now = now.AddMinutes(2);
        Assert.True(s.IsExpired);
    }

    [Fact]
    public async Task WithoutTools_NothingIsDeclared_AndThePromptSaysSo()
    {
        _client.Reply(new TextDelta("Abra Configurações > Impressoras."));
        var chat = Chat();

        await chat.AskAsync("como altero a impressora?");

        var request = _client.Requests.Single();
        Assert.Empty(request.Tools);
        Assert.Contains("Tools are not available", request.SystemPrompt);
        Assert.DoesNotContain(chat.Entries, e => e.Role == ChatEntryRole.Notice);   // not offering tools is not a surprise to explain
    }

    [Fact]
    public void WithTools_ThePromptHasNoSuchNote()
    {
        var builder = new SystemPromptBuilder(_catalog, "1.0", "26200");
        Assert.DoesNotContain("Tools are not available", builder.Build());
        Assert.Contains("Tools are not available", builder.Build(toolsAvailable: false));
        Assert.DoesNotContain("{{", builder.Build(toolsAvailable: false));
    }

    // ------------------------------------------------------------------------------------------ buttons

    private static AssistantStreamEvent Tool(string id, string name, string json) => new ToolCallReceived(new AssistantToolCall(id, name, json));

    private AssistantChat ChatWithTools(AssistantSession? session = null)
    {
        session ??= new AssistantSession();
        return new AssistantChat(() => new AssistantEngine(_client, new SystemPromptBuilder(_catalog, "1.0", "26200"), new AssistantTools(_catalog), session), session);
    }

    [Fact]
    public async Task ASuggestion_BecomesAButtonAfterTheAnswer_AndTheFirstOneIsSelected()
    {
        _client.Reply(new TextDelta("Abra a página de impressoras."),
            Tool("c1", "open_windows_setting", """{"uri":"ms-settings:printers"}"""),
            Tool("c2", "open_apparatus_screen", """{"screen":"snippets"}"""));
        var chat = ChatWithTools();

        Assert.Equal(AskOutcome.Answered, await chat.AskAsync("impressora"));

        Assert.Equal(new[] { ChatEntryRole.User, ChatEntryRole.Assistant, ChatEntryRole.Suggestion, ChatEntryRole.Suggestion }, chat.Entries.Select(e => e.Role));
        var buttons = chat.Suggestions;
        Assert.Equal(SuggestionKind.OpenWindowsSetting, buttons[0].Suggestion!.Kind);
        Assert.Equal("ms-settings:printers", buttons[0].Suggestion!.Payload);
        Assert.True(buttons[0].IsSelected);
        Assert.False(buttons[1].IsSelected);
        Assert.Same(buttons[0], chat.SelectedSuggestion);
        Assert.NotEmpty(buttons[0].Detail);
    }

    [Fact]
    public async Task Buttons_NeverRunAnything_AndTheHistoryKeepsTheAcknowledgement()
    {
        _client.Reply(new TextDelta("ok"), Tool("c1", "open_url", """{"url":"https://example.com"}"""));
        var chat = ChatWithTools();

        await chat.AskAsync("site");

        var tool = chat.Session.Messages.Single(m => m.Role == AssistantRole.Tool);
        Assert.Equal(AssistantTools.SuggestedResult, tool.Text);
    }

    [Fact]
    public async Task OnlyButtons_NoText_IsStillAnAnswer_AndTheQuestionStays()
    {
        _client.Reply(Tool("c1", "open_launcher", """{"query":"clip "}"""));
        var chat = ChatWithTools();

        Assert.Equal(AskOutcome.Answered, await chat.AskAsync("histórico"));

        Assert.Equal(new[] { ChatEntryRole.User, ChatEntryRole.Suggestion }, chat.Entries.Select(e => e.Role));
    }

    [Fact]
    public async Task OnlyRejectedCalls_AreExplained_AndCountAsFailed()
    {
        _client.Reply(Tool("c1", "open_windows_setting", """{"uri":"ms-settings:not-in-the-catalog"}"""));
        var chat = ChatWithTools();

        Assert.Equal(AskOutcome.Failed, await chat.AskAsync("x"));

        var only = Assert.Single(chat.Entries);
        Assert.Equal(ChatEntryRole.Notice, only.Role);
        Assert.Empty(chat.Suggestions);
    }

    [Fact]
    public async Task ARejectedCall_NextToAnAnswer_ShowsNothingExtra()
    {
        _client.Reply(new TextDelta("Veja."), Tool("c1", "unknown_tool", "{}"));
        var chat = ChatWithTools();

        await chat.AskAsync("x");

        Assert.Equal(new[] { ChatEntryRole.User, ChatEntryRole.Assistant }, chat.Entries.Select(e => e.Role));
    }

    [Fact]
    public async Task TheNextQuestion_RemovesTheOldButtons()
    {
        _client.Reply(new TextDelta("a"), Tool("c1", "open_apparatus_screen", """{"screen":"settings"}"""));
        _client.Reply(new TextDelta("b"));
        var chat = ChatWithTools();

        await chat.AskAsync("um");
        Assert.Single(chat.Suggestions);
        await chat.AskAsync("dois");

        Assert.Empty(chat.Suggestions);
        Assert.Null(chat.SelectedSuggestion);
    }

    [Fact]
    public async Task MovingTheSelection_WrapsAround_AndClickingSelects()
    {
        _client.Reply(new TextDelta("x"),
            Tool("c1", "open_apparatus_screen", """{"screen":"settings"}"""),
            Tool("c2", "open_apparatus_screen", """{"screen":"snippets"}"""),
            Tool("c3", "open_apparatus_screen", """{"screen":"actions"}"""));
        var chat = ChatWithTools();
        await chat.AskAsync("x");
        var b = chat.Suggestions;

        Assert.Same(b[1], chat.MoveSuggestion(1));
        Assert.Same(b[2], chat.MoveSuggestion(1));
        Assert.Same(b[0], chat.MoveSuggestion(1));
        Assert.Same(b[2], chat.MoveSuggestion(-1));
        Assert.Single(b, e => e.IsSelected);

        chat.SelectSuggestion(b[1]);
        Assert.Same(b[1], chat.SelectedSuggestion);
        Assert.Single(b, e => e.IsSelected);
    }

    [Fact]
    public void WithoutButtons_MovingTheSelectionDoesNothing()
    {
        var chat = ChatWithTools();
        Assert.Null(chat.MoveSuggestion(1));
        Assert.Null(chat.SelectedSuggestion);
    }

    [Fact]
    public async Task ACancelledQuestion_LeavesNoButtonsBehind()
    {
        _client.Reply(new TextDelta("x"), Tool("c1", "open_apparatus_screen", """{"screen":"settings"}"""));
        var chat = ChatWithTools();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Equal(AskOutcome.Cancelled, await chat.AskAsync("x", cts.Token));
        Assert.Empty(chat.Entries);
    }

    [Fact]
    public async Task ToolsAreDeclared_WhenTheEngineOffersThem()
    {
        _client.Reply(new TextDelta("x"));
        var chat = ChatWithTools();

        await chat.AskAsync("x");

        var request = _client.Requests.Single();
        Assert.Contains(request.Tools, t => t.Name == AssistantTools.OpenWindowsSetting);
        Assert.DoesNotContain("Tools are not available", request.SystemPrompt);
    }

    [Fact]
    public async Task KeepConversationSetting_DefaultsToOn_AndSurvivesCloning()
    {
        Assert.True(new Apparatus.Core.Settings.AppSettings().AssistantKeepConversation);
        var s = new Apparatus.Core.Settings.AppSettings { AssistantKeepConversation = false };
        Assert.False(s.Clone().AssistantKeepConversation);
        await Task.CompletedTask;
    }

    [Fact]
    public void KeepOpenSetting_DefaultsToOff_SurvivesCloning_AndTravelsInBackups()
    {
        Assert.False(new Apparatus.Core.Settings.AppSettings().AssistantKeepOpen);
        var s = new Apparatus.Core.Settings.AppSettings { AssistantKeepOpen = true };
        Assert.True(s.Clone().AssistantKeepOpen);
        Assert.True(Apparatus.Core.Backup.BackupService.ForExport(s).AssistantKeepOpen);
    }
}
