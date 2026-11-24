using System.Runtime.CompilerServices;
using System.Text.Json;
using Apparatus.Core.Actions;
using Apparatus.Core.Assistant;
using Apparatus.Core.Snippets;
using Microsoft.Extensions.AI;

namespace Apparatus.Core.Tests;

/// <summary>A scripted IChatClient: whatever it is told to stream, and a record of what it was sent.</summary>
internal sealed class ScriptedChatClient : IChatClient
{
    public Func<IList<ChatResponseUpdate>>? Script { get; set; }
    public Exception? ThrowOnStart { get; set; }
    public List<ChatMessage> Sent { get; } = new();
    public ChatOptions? SentOptions { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Record(messages, options);
        if (ThrowOnStart is not null) throw ThrowOnStart;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Record(messages, options);
        if (ThrowOnStart is not null) throw ThrowOnStart;
        foreach (var update in Script?.Invoke() ?? Array.Empty<ChatResponseUpdate>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return update;
        }
    }

    private void Record(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        Sent.Clear();
        Sent.AddRange(messages);
        SentOptions = options;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    public static ChatResponseUpdate Text(string text) => new(ChatRole.Assistant, text);

    public static ChatResponseUpdate Call(string id, string name, string argsJson) => new()
    {
        Role = ChatRole.Assistant,
        Contents = new List<AIContent> { new FunctionCallContent(id, name, JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson)) },
    };
}

public class ChatClientAdapterTests
{
    private static AssistantRequest Request(params AssistantMessage[] messages) =>
        new("SYSTEM PROMPT", messages, new[] { new AssistantToolSpec("open_launcher", "Opens the launcher.", """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""") });

    private static async Task<List<AssistantStreamEvent>> Collect(IAssistantClient client, AssistantRequest request, CancellationToken ct = default)
    {
        var list = new List<AssistantStreamEvent>();
        await foreach (var e in client.StreamAsync(request, ct)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Text_And_ToolCalls_AreSeparated_InOrder()
    {
        var chat = new ScriptedChatClient
        {
            Script = () => new[]
            {
                ScriptedChatClient.Text("Abra "),
                ScriptedChatClient.Text("o launcher."),
                ScriptedChatClient.Call("call_9", "open_launcher", """{"query":"clip "}"""),
            },
        };
        var events = await Collect(new ChatClientAdapter(chat), Request(AssistantMessage.User("hi")));

        Assert.Equal("Abra ", ((TextDelta)events[0]).Text);
        Assert.Equal("o launcher.", ((TextDelta)events[1]).Text);
        var call = ((ToolCallReceived)events[2]).Call;
        Assert.Equal("call_9", call.Id);
        Assert.Equal("open_launcher", call.Name);
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal("clip ", doc.RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public async Task ToolCalls_ComeAfterTheText_EvenIfTheyArriveFirst_AndDuplicatesCollapse()
    {
        var chat = new ScriptedChatClient
        {
            Script = () => new[]
            {
                ScriptedChatClient.Call("c1", "open_launcher", """{"query":"a"}"""),
                ScriptedChatClient.Call("c1", "open_launcher", """{"query":"a"}"""),
                ScriptedChatClient.Text("text"),
            },
        };
        var events = await Collect(new ChatClientAdapter(chat), Request(AssistantMessage.User("hi")));
        Assert.IsType<TextDelta>(events[0]);
        Assert.Single(events.OfType<ToolCallReceived>());
    }

    [Fact]
    public async Task MissingCallIds_GetGeneratedOnes_SoTheHistoryCanPairThem()
    {
        var chat = new ScriptedChatClient
        {
            Script = () => new[]
            {
                ScriptedChatClient.Call("", "open_launcher", """{"query":"a"}"""),
                ScriptedChatClient.Call("", "open_launcher", """{"query":"b"}"""),
            },
        };
        var calls = (await Collect(new ChatClientAdapter(chat), Request(AssistantMessage.User("hi")))).OfType<ToolCallReceived>().Select(c => c.Call).ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.False(string.IsNullOrWhiteSpace(c.Id)));
        Assert.NotEqual(calls[0].Id, calls[1].Id);
    }

    [Fact]
    public async Task Tools_AreDeclaredOnly_NeverInvokable_AndTheSystemPromptLeads()
    {
        var chat = new ScriptedChatClient { Script = () => new[] { ScriptedChatClient.Text("x") } };
        await Collect(new ChatClientAdapter(chat), Request(AssistantMessage.User("hi")));

        Assert.Equal(ChatRole.System, chat.Sent[0].Role);
        Assert.Equal("SYSTEM PROMPT", chat.Sent[0].Text);
        var tool = Assert.Single(chat.SentOptions!.Tools!);
        Assert.IsAssignableFrom<AIFunctionDeclaration>(tool);
        Assert.False(tool is AIFunction, "a declaration must not be something the SDK could call by itself");
        Assert.Equal("open_launcher", tool.Name);
    }

    [Fact]
    public async Task NoTools_MeansNoToolsField()
    {
        var chat = new ScriptedChatClient { Script = () => new[] { ScriptedChatClient.Text("x") } };
        await Collect(new ChatClientAdapter(chat), new AssistantRequest("S", new[] { AssistantMessage.User("hi") }, Array.Empty<AssistantToolSpec>()));
        Assert.True(chat.SentOptions!.Tools is null or { Count: 0 });
    }

    [Fact]
    public async Task History_IsMapped_WithToolCallsAndTheirResults()
    {
        var chat = new ScriptedChatClient { Script = () => new[] { ScriptedChatClient.Text("x") } };
        var call = new AssistantToolCall("c1", "open_launcher", """{"query":"clip"}""");
        await Collect(new ChatClientAdapter(chat), Request(
            AssistantMessage.User("first"),
            AssistantMessage.Assistant("Here.", new[] { call }),
            AssistantMessage.ToolResult("c1", AssistantTools.SuggestedResult),
            AssistantMessage.User("second")));

        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User }, chat.Sent.Select(m => m.Role));
        var assistant = chat.Sent[2];
        Assert.Contains(assistant.Contents, c => c is TextContent { Text: "Here." });
        var fc = Assert.IsType<FunctionCallContent>(assistant.Contents.Single(c => c is FunctionCallContent));
        Assert.Equal("c1", fc.CallId);
        Assert.Equal("open_launcher", fc.Name);
        var fr = Assert.IsType<FunctionResultContent>(chat.Sent[3].Contents.Single());
        Assert.Equal("c1", fr.CallId);
        Assert.Equal(AssistantTools.SuggestedResult, fr.Result?.ToString());
    }

    [Fact]
    public async Task ProviderFailures_BecomeAssistantExceptions_InPortuguese()
    {
        async Task<AssistantException> Fail(Exception inner, bool local = false)
        {
            var chat = new ScriptedChatClient { ThrowOnStart = inner };
            return await Assert.ThrowsAsync<AssistantException>(() => Collect(new ChatClientAdapter(chat, localProvider: local), Request(AssistantMessage.User("hi"))));
        }

        Assert.Equal(AssistantErrorKind.Network, (await Fail(new HttpRequestException("boom"))).Kind);
        Assert.Contains("Ollama", (await Fail(new HttpRequestException("refused"), local: true)).Message);
        Assert.Equal(AssistantErrorKind.Timeout, (await Fail(new TaskCanceledException("timeout"))).Kind);
        Assert.Equal(AssistantErrorKind.Timeout, (await Fail(new TimeoutException())).Kind);
    }

    [Fact]
    public async Task ACallerCancellation_StaysACancellation()
    {
        using var cts = new CancellationTokenSource();
        var chat = new ScriptedChatClient { Script = () => new[] { ScriptedChatClient.Text("a"), ScriptedChatClient.Text("b") } };
        var adapter = new ChatClientAdapter(chat);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in adapter.StreamAsync(Request(AssistantMessage.User("hi")), cts.Token))
                cts.Cancel();
        });
    }

    [Fact]
    public async Task ListModels_And_Test_TranslateErrors()
    {
        var chat = new ScriptedChatClient { ThrowOnStart = new HttpRequestException("down") };
        var adapter = new ChatClientAdapter(chat, ct => throw new HttpRequestException("down"));
        Assert.Equal(AssistantErrorKind.Network, (await Assert.ThrowsAsync<AssistantException>(() => adapter.ListModelsAsync(default))).Kind);
        Assert.Equal(AssistantErrorKind.Network, (await Assert.ThrowsAsync<AssistantException>(() => adapter.TestAsync(default))).Kind);

        var ok = new ChatClientAdapter(new ScriptedChatClient(), _ => Task.FromResult<IReadOnlyList<string>>(new[] { "m1", "m2" }));
        Assert.Equal(new[] { "m1", "m2" }, await ok.ListModelsAsync(default));
        await ok.TestAsync(default);
    }
}

internal sealed class FakeAssistantClient : IAssistantClient
{
    public Queue<Func<AssistantRequest, IEnumerable<AssistantStreamEvent>>> Answers { get; } = new();
    public List<AssistantRequest> Requests { get; } = new();

    /// <summary>When set, the answer stops after its first event until this completes (an answer "in progress", without blocking a thread).</summary>
    public TaskCompletionSource? PauseAfterFirst { get; set; }

    public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(AssistantRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Requests.Add(request);
        bool first = true;
        foreach (var e in Answers.Dequeue()(request))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return e;
            if (first && PauseAfterFirst is { } gate)
            {
                first = false;
                await gate.Task.WaitAsync(cancellationToken);
            }
            first = false;
        }
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task TestAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Reply(params AssistantStreamEvent[] events) => Answers.Enqueue(_ => events);
    public void Fail(AssistantException ex) => Answers.Enqueue(_ => throw ex);
}

public class AssistantEngineTests : IDisposable
{
    private readonly FakeAssistantClient _client = new();
    private readonly ActionStore _actions = new(":memory:");
    private readonly SnippetStore _snippets = new(":memory:");
    private readonly SettingsCatalog _catalog = SettingsCatalog.LoadEmbedded();

    public void Dispose()
    {
        _actions.Dispose();
        _snippets.Dispose();
    }

    private AssistantEngine Engine(AssistantSession? session = null, IReadOnlyList<string>? shared = null, IReadOnlyList<ActionDefinition>? userActions = null)
    {
        var tools = new AssistantTools(_catalog, new AssistantToolOptions
        {
            UserActions = userActions ?? Array.Empty<ActionDefinition>(),
            ValidateAction = _actions.Validate,
            ValidateSnippetTrigger = t => _snippets.ValidateTrigger(t),
        });
        return new AssistantEngine(_client, new SystemPromptBuilder(_catalog, "1.0", "26200"), tools, session ?? new AssistantSession(), shared is null ? null : () => shared);
    }

    private static async Task<List<AssistantUiEvent>> Ask(AssistantEngine engine, string question, CancellationToken ct = default)
    {
        var list = new List<AssistantUiEvent>();
        await foreach (var e in engine.AskAsync(question, ct)) list.Add(e);
        return list;
    }

    private static AssistantStreamEvent Tool(string id, string name, string json) => new ToolCallReceived(new AssistantToolCall(id, name, json));

    [Fact]
    public async Task TheMainScenario_APrinterQuestion_GivesTextAndAButton_AndRunsNothing()
    {
        _client.Reply(new TextDelta("Impressoras e scanners "), new TextDelta("fica aqui."), Tool("c1", "open_windows_setting", """{"uri":"ms-settings:printers"}"""));
        var engine = Engine();

        var events = await Ask(engine, "Como altero as configurações de impressoras?");

        Assert.Equal("Impressoras e scanners fica aqui.", string.Concat(events.OfType<AnswerText>().Select(t => t.Delta)));
        var offered = Assert.Single(events.OfType<SuggestionOffered>()).Action;
        Assert.Equal(SuggestionKind.OpenWindowsSetting, offered.Kind);
        Assert.Equal("ms-settings:printers", offered.Payload);
        Assert.IsType<SuggestionOffered>(events[^1]);   // buttons come after the text
        AssistantSessionTests.AssertValidForTheApi(engine.Session.Messages);
    }

    [Fact]
    public async Task AClassicTool_IsFollowedByItsModernPage_AndTheHistoryKnowsOnlyTheModelsCall()
    {
        _client.Reply(new TextDelta("Aqui."), Tool("c1", "open_windows_setting", """{"uri":"appwiz.cpl"}"""));
        var engine = Engine();

        var events = await Ask(engine, "como desinstalo um programa?");

        var offered = events.OfType<SuggestionOffered>().Select(o => o.Action).ToList();
        Assert.Equal(new[] { "appwiz.cpl", "ms-settings:appsfeatures" }, offered.Select(a => a.Payload));
        Assert.Contains("Alternativa moderna", offered[1].Description);
        Assert.Equal(SuggestionKind.OpenWindowsSetting, offered[1].Kind);
        // The extra button is the app's, not the model's: one call, one acknowledgement in the history.
        AssistantSessionTests.AssertValidForTheApi(engine.Session.Messages);
        Assert.Single(engine.Session.Messages, m => m.Role == AssistantRole.Tool);
        // And the prompt did not change to explain it.
        Assert.DoesNotContain("moderna", _client.Requests.Single().SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheModernPage_IsNotDuplicated_WhenTheModelAlreadySuggestedIt_InEitherOrder()
    {
        _client.Reply(Tool("c1", "open_windows_setting", """{"uri":"ms-settings:appsfeatures"}"""), Tool("c2", "open_windows_setting", """{"uri":"appwiz.cpl"}"""));
        var events = await Ask(Engine(), "desinstalar");
        Assert.Equal(new[] { "ms-settings:appsfeatures", "appwiz.cpl" },
            events.OfType<SuggestionOffered>().Select(o => o.Action.Payload));

        _client.Reply(Tool("c1", "open_windows_setting", """{"uri":"appwiz.cpl"}"""), Tool("c2", "open_windows_setting", """{"uri":"ms-settings:appsfeatures"}"""));
        events = await Ask(Engine(), "desinstalar");
        Assert.Equal(new[] { "appwiz.cpl", "ms-settings:appsfeatures" },
            events.OfType<SuggestionOffered>().Select(o => o.Action.Payload));
    }

    [Fact]
    public async Task ModernPages_DoNotCountTowardTheLimit_AndToolsWithoutOneGetNoExtra()
    {
        _client.Reply(
            Tool("c1", "open_windows_setting", """{"uri":"appwiz.cpl"}"""),
            Tool("c2", "open_windows_setting", """{"uri":"mmsys.cpl"}"""),
            Tool("c3", "open_windows_setting", """{"uri":"devmgmt.msc"}"""));
        var events = await Ask(Engine(), "vários");
        Assert.Equal(new[] { "appwiz.cpl", "ms-settings:appsfeatures", "mmsys.cpl", "ms-settings:sound", "devmgmt.msc" },
            events.OfType<SuggestionOffered>().Select(o => o.Action.Payload));
    }

    [Fact]
    public async Task TheRequest_CarriesThePrompt_TheCatalog_TheQuestion_AndTheTools()
    {
        _client.Reply(new TextDelta("ok"));
        await Ask(Engine(), "hello");

        var request = _client.Requests.Single();
        Assert.Contains("ms-settings:printers — ", request.SystemPrompt);
        Assert.Equal("hello", request.Messages.Single().Text);
        Assert.Contains(request.Tools, t => t.Name == "open_windows_setting");
        Assert.DoesNotContain(request.Tools, t => t.Name == "run_my_action");
    }

    [Fact]
    public async Task SharedActionNames_GoIntoThePrompt_OnlyWhenGiven()
    {
        _client.Reply(new TextDelta("ok"));
        _client.Reply(new TextDelta("ok"));
        await Ask(Engine(), "a");
        await Ask(Engine(shared: new[] { "Pausar música" }), "b");

        Assert.DoesNotContain("Pausar música", _client.Requests[0].SystemPrompt);
        Assert.Contains("Pausar música", _client.Requests[1].SystemPrompt);
    }

    [Fact]
    public async Task RejectedCalls_AreReported_NotOffered_AndTheHistoryStillPairsThem()
    {
        _client.Reply(new TextDelta("Vou abrir."), Tool("c1", "open_windows_setting", """{"uri":"ms-settings:does-not-exist"}"""), Tool("c2", "delete_everything", "{}"));
        var engine = Engine();

        var events = await Ask(engine, "q");

        Assert.Empty(events.OfType<SuggestionOffered>());
        Assert.Equal(2, events.OfType<SuggestionRejected>().Count());
        var messages = engine.Session.Messages;
        Assert.Equal(2, messages.Count(m => m.Role == AssistantRole.Tool));
        Assert.All(messages.Where(m => m.Role == AssistantRole.Tool), m => Assert.StartsWith("Rejected:", m.Text));
        AssistantSessionTests.AssertValidForTheApi(messages);
    }

    [Fact]
    public async Task ASecondQuestion_SendsAValidHistory_WithTheSyntheticToolResult()
    {
        _client.Reply(new TextDelta("Aqui."), Tool("c1", "open_windows_setting", """{"uri":"ms-settings:printers"}"""));
        _client.Reply(new TextDelta("Segunda."));
        var engine = Engine();

        await Ask(engine, "primeira");
        await Ask(engine, "segunda");

        var sent = _client.Requests[1].Messages;
        Assert.Equal(new[] { AssistantRole.User, AssistantRole.Assistant, AssistantRole.Tool, AssistantRole.User }, sent.Select(m => m.Role));
        Assert.Equal(AssistantTools.SuggestedResult, sent[2].Text);
        AssistantSessionTests.AssertValidForTheApi(sent);
    }

    [Fact]
    public async Task OnlyThreeSuggestions_PerAnswer()
    {
        _client.Reply(Enumerable.Range(1, 5).Select(i => Tool($"c{i}", "open_launcher", $"{{\"query\":\"q{i}\"}}")).ToArray());
        var events = await Ask(Engine(), "q");
        Assert.Equal(3, events.OfType<SuggestionOffered>().Count());
        Assert.Equal(2, events.OfType<SuggestionRejected>().Count());
    }

    [Fact]
    public async Task AProviderError_TakesTheQuestionBackOutOfTheHistory()
    {
        _client.Fail(AssistantErrors.FromStatus(429, null));
        var engine = Engine();

        var ex = await Assert.ThrowsAsync<AssistantException>(() => Ask(engine, "q"));

        Assert.Equal(AssistantErrorKind.RateLimited, ex.Kind);
        Assert.Empty(engine.Session.Messages);
    }

    [Fact]
    public async Task Cancelling_TakesTheQuestionBack_AndKeepsEarlierTurns()
    {
        _client.Reply(new TextDelta("first answer"));
        var engine = Engine();
        await Ask(engine, "one");

        using var cts = new CancellationTokenSource();
        _client.Reply(new TextDelta("a"), new TextDelta("b"), new TextDelta("c"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in engine.AskAsync("two", cts.Token)) cts.Cancel();
        });

        Assert.Equal(2, engine.Session.Messages.Count);
        Assert.Equal("one", engine.Session.Messages[0].Text);
    }

    [Fact]
    public async Task AnEmptyAnswer_IsReported_AndNotKept()
    {
        _client.Reply();
        var engine = Engine();
        var events = await Ask(engine, "q");
        Assert.IsType<AssistantNotice>(Assert.Single(events));
        Assert.Empty(engine.Session.Messages);
    }

    [Fact]
    public async Task AModelThatRefusesTools_IsRetriedOnceWithoutThem_AndRemembered()
    {
        _client.Fail(AssistantErrors.FromStatus(400, "tools not supported"));
        _client.Reply(new TextDelta("Só texto."));
        _client.Reply(new TextDelta("De novo só texto."));
        var engine = Engine();

        var first = await Ask(engine, "um");
        Assert.Contains(first, e => e is AssistantNotice);
        Assert.Equal("Só texto.", string.Concat(first.OfType<AnswerText>().Select(t => t.Delta)));
        Assert.NotEmpty(_client.Requests[0].Tools);
        Assert.Empty(_client.Requests[1].Tools);

        await Ask(engine, "dois");
        Assert.Empty(_client.Requests[2].Tools);   // does not try again on every question
        Assert.Equal(3, _client.Requests.Count);
    }

    [Fact]
    public async Task ABadRequest_ThatPersistsWithoutTools_IsNotRetriedForever()
    {
        _client.Fail(AssistantErrors.FromStatus(400, "bad"));
        _client.Fail(AssistantErrors.FromStatus(400, "still bad"));
        var engine = Engine();
        await Assert.ThrowsAsync<AssistantException>(() => Ask(engine, "q"));
        Assert.Equal(2, _client.Requests.Count);
        Assert.Empty(engine.Session.Messages);
    }

    [Fact]
    public async Task AnExpiredSession_IsClearedBeforeTheNextQuestion()
    {
        var now = DateTime.UtcNow;
        var session = new AssistantSession(idleTimeout: TimeSpan.FromMinutes(5), clock: () => now);
        _client.Reply(new TextDelta("a"));
        _client.Reply(new TextDelta("b"));
        var engine = Engine(session);

        await Ask(engine, "old");
        now = now.AddMinutes(30);
        await Ask(engine, "new");

        Assert.Equal("new", Assert.Single(_client.Requests[1].Messages).Text);
    }

    [Fact]
    public async Task ABlankQuestion_DoesNothing()
    {
        var engine = Engine();
        Assert.Empty(await Ask(engine, "   "));
        Assert.Empty(_client.Requests);
        Assert.Empty(engine.Session.Messages);
    }
}
