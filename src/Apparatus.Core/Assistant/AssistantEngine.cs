using System.Runtime.CompilerServices;
using System.Text;

namespace Apparatus.Core.Assistant;

/// <summary>What the UI receives while an answer is produced.</summary>
public abstract record AssistantUiEvent;

/// <summary>A piece of the answer text, to append as it arrives.</summary>
public sealed record AnswerText(string Delta) : AssistantUiEvent;

/// <summary>A validated suggestion; the UI shows it as a button. Nothing has run.</summary>
public sealed record SuggestionOffered(SuggestedAction Action) : AssistantUiEvent;

/// <summary>A tool call the app threw away (unknown tool, value outside the allowed list...). For logging; the user needs not see it.</summary>
public sealed record SuggestionRejected(string ToolName, string Reason) : AssistantUiEvent;

/// <summary>A one-line note for the user (for example "this model only answers with text").</summary>
public sealed record AssistantNotice(string Text) : AssistantUiEvent;

/// <summary>
/// One question, one round: builds the prompt, streams the answer, validates the tool calls the model made and
/// keeps the history valid. It never feeds a tool result back to the model to get a second answer, and it never runs anything.
/// </summary>
public sealed class AssistantEngine
{
    private readonly IAssistantClient _client;
    private readonly SystemPromptBuilder _prompt;
    private readonly AssistantTools _tools;
    private readonly Func<IReadOnlyList<string>>? _sharedActionNames;
    private readonly bool _offerTools;
    private bool _toolsRefused;

    /// <param name="offerTools">False answers with text only: no tools are declared, so the model has no reason to talk about buttons that the screen cannot show.</param>
    public AssistantEngine(IAssistantClient client, SystemPromptBuilder prompt, AssistantTools tools, AssistantSession session,
        Func<IReadOnlyList<string>>? sharedActionNames = null, bool offerTools = true)
    {
        _offerTools = offerTools;
        _client = client;
        _prompt = prompt;
        _tools = tools;
        Session = session;
        _sharedActionNames = sharedActionNames;
    }

    public AssistantSession Session { get; }

    /// <summary>
    /// Sends the question and streams the outcome. Throws <see cref="AssistantException"/> for a provider failure and
    /// <see cref="OperationCanceledException"/> when cancelled; in both cases the unanswered question is taken back out of the history.
    /// </summary>
    public async IAsyncEnumerable<AssistantUiEvent> AskAsync(string question, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        question = question.Trim();
        if (question.Length == 0) yield break;

        if (Session.IsExpired) Session.Clear();
        Session.AddUser(question);

        bool answered = false;
        try
        {
            var specs = _toolsRefused || !_offerTools ? Array.Empty<AssistantToolSpec>() : _tools.Specs();
            var request = new AssistantRequest(_prompt.Build(_sharedActionNames?.Invoke(), toolsAvailable: specs.Count > 0), Session.Messages, specs);

            var text = new StringBuilder();
            var calls = new List<AssistantToolCall>();

            for (int attempt = 0; ; attempt++)
            {
                bool retryWithoutTools = false;
                var stream = _client.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
                try
                {
                    while (true)
                    {
                        bool has;
                        try
                        {
                            has = await stream.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (AssistantException ex) when (attempt == 0 && request.Tools.Count > 0 && ex.Kind == AssistantErrorKind.BadRequest && text.Length == 0 && calls.Count == 0)
                        {
                            retryWithoutTools = true;
                            break;
                        }
                        if (!has) break;

                        switch (stream.Current)
                        {
                            case TextDelta t:
                                text.Append(t.Text);
                                yield return new AnswerText(t.Text);
                                break;
                            case ToolCallReceived c:
                                calls.Add(c.Call);
                                break;
                        }
                    }
                }
                finally
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }

                if (!retryWithoutTools) break;

                // Many small local models reject the "tools" field outright: answer with text only, and remember it.
                _toolsRefused = true;
                request = request with { Tools = Array.Empty<AssistantToolSpec>(), SystemPrompt = _prompt.Build(_sharedActionNames?.Invoke(), toolsAvailable: false) };
                yield return new AssistantNotice("Este modelo não aceita ferramentas; ele responde só com texto, sem botões de ação.");
            }

            var results = _tools.Evaluate(calls);
            if (text.Length == 0 && results.Count == 0)
            {
                yield return new AssistantNotice("O modelo devolveu uma resposta vazia. Tente de novo ou escolha outro modelo.");
                yield break;   // not answered: the question is taken back below
            }

            Session.AddAssistant(text.ToString(), results.Select(r => (r.Call,
                r.Accepted ? AssistantTools.SuggestedResult : $"Rejected: {r.Rejection}")).ToList());
            answered = true;

            foreach (var r in results)
            {
                yield return r.Accepted
                    ? new SuggestionOffered(r.Action!)
                    : new SuggestionRejected(r.Call.Name, r.Rejection ?? "");
            }
        }
        finally
        {
            if (!answered) Session.RollBackPendingUser();
        }
    }
}
