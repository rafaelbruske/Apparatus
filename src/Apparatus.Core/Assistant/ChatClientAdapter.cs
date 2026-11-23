using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Apparatus.Core.Assistant;

/// <summary>
/// The address of a provider. Only HTTPS is accepted, except for a server on this very machine (Ollama, LM Studio):
/// the key travels in a header and must never cross the network in the clear.
/// </summary>
public static class AssistantEndpoint
{
    public static bool IsLocal(Uri uri) =>
        uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>Null when the address is fine, otherwise a message for the user.</summary>
    public static string? Validate(string baseUrl, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var parsed)) return "O endereço do provedor não é uma URL válida.";
        if (parsed.UserInfo.Length > 0) return "O endereço não pode conter usuário ou senha.";
        bool ok = parsed.Scheme == Uri.UriSchemeHttps || (parsed.Scheme == Uri.UriSchemeHttp && IsLocal(parsed));
        if (!ok) return "Use um endereço https:// (http:// só é aceito para um servidor nesta máquina, como o Ollama).";

        // The SDK appends "chat/completions" to the path, which only works with a trailing slash.
        uri = parsed.AbsolutePath.EndsWith('/') ? parsed : new Uri(parsed.GetLeftPart(UriPartial.Path) + "/" + parsed.Query);
        return null;
    }
}

/// <summary>
/// <see cref="IAssistantClient"/> over Microsoft.Extensions.AI. Tools are only declared, never invoked here:
/// a call the model makes comes back to the app as a <see cref="ToolCallReceived"/> for validation.
/// </summary>
public sealed class ChatClientAdapter : IAssistantClient
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly IChatClient _chat;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _listModels;
    private readonly bool _local;
    private readonly string? _secret;
    private readonly TimeSpan _timeout;

    /// <summary>For tests and for callers that build the <see cref="IChatClient"/> themselves.</summary>
    public ChatClientAdapter(
        IChatClient chat,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? listModels = null,
        bool localProvider = false,
        string? secretToRedact = null,
        TimeSpan? timeout = null)
    {
        _chat = chat;
        _listModels = listModels ?? (_ => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));
        _local = localProvider;
        _secret = secretToRedact;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>A client for one provider and model. The key goes only to <paramref name="baseUrl"/>, in the Authorization header.</summary>
    public static ChatClientAdapter Create(string baseUrl, string? apiKey, string model, TimeSpan? timeout = null)
    {
        if (AssistantEndpoint.Validate(baseUrl, out var endpoint) is { } problem)
            throw new AssistantException(AssistantErrorKind.InvalidEndpoint, problem);
        if (string.IsNullOrWhiteSpace(model))
            throw new AssistantException(AssistantErrorKind.ModelNotFound, "Escolha um modelo nas configurações do assistente.");

        bool local = AssistantEndpoint.IsLocal(endpoint!);
        string key = string.IsNullOrWhiteSpace(apiKey) ? (local ? "local" : throw AssistantErrors.MissingKey()) : apiKey.Trim();
        var limit = timeout ?? DefaultTimeout;

        // No automatic retries: repeating a 429 only burns the free quota. The user sees the error and decides.
        var http = new HttpClient { Timeout = limit };
        var options = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            Transport = new HttpClientPipelineTransport(http),
            RetryPolicy = new ClientRetryPolicy(0),
        };
        var client = new OpenAIClient(new ApiKeyCredential(key), options);

        return new ChatClientAdapter(
            client.GetChatClient(model).AsIChatClient(),
            async ct =>
            {
                var models = await client.GetOpenAIModelClient().GetModelsAsync(ct).ConfigureAwait(false);
                return models.Value
                    .Select(m => m.Id.StartsWith("models/", StringComparison.Ordinal) ? m.Id["models/".Length..] : m.Id)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
            },
            local, key, limit);
    }

    public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
        AssistantRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var messages = ToChatMessages(request);
        var options = new ChatOptions { Temperature = 0.2f };
        if (request.Tools.Count > 0) options.Tools = request.Tools.Select(ToDeclaration).Cast<AITool>().ToList();

        var calls = new List<AssistantToolCall>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        await using var stream = _chat.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool has;
            try
            {
                has = await stream.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (Translate(ex, cancellationToken) is { } translated)
            {
                throw translated;
            }
            if (!has) break;

            foreach (var content in stream.Current.Contents)
            {
                switch (content)
                {
                    case TextContent { Text.Length: > 0 } text:
                        yield return new TextDelta(text.Text);
                        break;
                    case FunctionCallContent call:
                        // Some providers (local ones, mostly) leave the id empty; the history needs one to pair the reply.
                        string id = string.IsNullOrWhiteSpace(call.CallId) ? $"call_{calls.Count + 1}" : call.CallId;
                        if (seen.Add(id)) calls.Add(new AssistantToolCall(id, call.Name, ArgumentsToJson(call)));
                        break;
                }
            }
        }

        foreach (var call in calls) yield return new ToolCallReceived(call);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _listModels(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _chat.GetResponseAsync("Reply with just: ok", new ChatOptions { MaxOutputTokens = 16, Temperature = 0 }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    // ------------------------------------------------------------------------------------------------ mapping

    internal static List<ChatMessage> ToChatMessages(AssistantRequest request)
    {
        var list = new List<ChatMessage> { new(ChatRole.System, request.SystemPrompt) };
        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case AssistantRole.User:
                    list.Add(new ChatMessage(ChatRole.User, m.Text));
                    break;
                case AssistantRole.Assistant:
                    var parts = new List<AIContent>();
                    if (m.Text.Length > 0) parts.Add(new TextContent(m.Text));
                    foreach (var c in m.ToolCalls ?? Array.Empty<AssistantToolCall>())
                        parts.Add(new FunctionCallContent(c.Id, c.Name, ParseArguments(c.ArgumentsJson)));
                    if (parts.Count > 0) list.Add(new ChatMessage(ChatRole.Assistant, parts));
                    break;
                case AssistantRole.Tool:
                    list.Add(new ChatMessage(ChatRole.Tool, new AIContent[] { new FunctionResultContent(m.ToolCallId ?? "", m.Text) }));
                    break;
            }
        }
        return list;
    }

    private static AIFunctionDeclaration ToDeclaration(AssistantToolSpec spec)
    {
        using var doc = JsonDocument.Parse(spec.ParametersSchema);
        return AIFunctionFactory.CreateDeclaration(spec.Name, spec.Description, doc.RootElement.Clone());
    }

    private static Dictionary<string, object?> ParseArguments(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    /// <summary>Empty text (not "{}") when the SDK could not parse what the model sent, so the validator rejects it.</summary>
    private static string ArgumentsToJson(FunctionCallContent call)
    {
        if (call.Exception is not null) return "";
        try
        {
            return JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>());
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return "";
        }
    }

    // ------------------------------------------------------------------------------------------------ errors

    /// <summary>Null means "not ours, let it propagate" (including the caller's own cancellation).</summary>
    private AssistantException? Translate(Exception ex, CancellationToken caller)
    {
        if (ex is AssistantException known) return known;
        if (caller.IsCancellationRequested && ex is OperationCanceledException) return null;

        switch (ex)
        {
            case ClientResultException cre:
                return AssistantErrors.FromStatus(cre.Status, ProviderMessage(cre), _local, ex);
            case HttpRequestException:
                return AssistantErrors.Network(_local, ex);
            case TimeoutException:
            case OperationCanceledException:
                return AssistantErrors.Timeout(_timeout, ex);
            default:
                return null;
        }
    }

    /// <summary>The provider's own explanation (OpenAI-style {"error":{"message":..}} or Gemini's array form), with the key removed.</summary>
    private string? ProviderMessage(ClientResultException ex)
    {
        try
        {
            string? body = ex.GetRawResponse()?.Content?.ToString();
            if (string.IsNullOrWhiteSpace(body)) return null;
            string? message = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
                {
                    message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m.GetString()
                        : error.ValueKind == JsonValueKind.String ? error.GetString() : null;
                }
            }
            catch (JsonException) { /* not JSON: fall back to the raw text */ }
            return AssistantErrors.Redact(message ?? body, _secret);
        }
        catch
        {
            return null;
        }
    }
}
