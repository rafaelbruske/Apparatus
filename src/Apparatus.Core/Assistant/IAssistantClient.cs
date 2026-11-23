namespace Apparatus.Core.Assistant;

public enum AssistantRole
{
    User,
    Assistant,
    /// <summary>The answer to a tool call the assistant made (see <see cref="AssistantMessage.ToolCallId"/>).</summary>
    Tool,
}

/// <summary>A tool the model asked for. <see cref="ArgumentsJson"/> is untrusted: it is whatever the model wrote.</summary>
public sealed record AssistantToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>One message of the conversation, in the app's own terms (no SDK types leave the adapter).</summary>
public sealed record AssistantMessage(
    AssistantRole Role,
    string Text,
    IReadOnlyList<AssistantToolCall>? ToolCalls = null,
    string? ToolCallId = null)
{
    public static AssistantMessage User(string text) => new(AssistantRole.User, text);

    public static AssistantMessage Assistant(string text, IReadOnlyList<AssistantToolCall>? calls = null) =>
        new(AssistantRole.Assistant, text, calls is { Count: > 0 } ? calls : null);

    public static AssistantMessage ToolResult(string callId, string text) => new(AssistantRole.Tool, text, null, callId);
}

/// <summary>A tool offered to the model. <see cref="ParametersSchema"/> is a JSON Schema object, as text.</summary>
public sealed record AssistantToolSpec(string Name, string Description, string ParametersSchema);

public sealed record AssistantRequest(
    string SystemPrompt,
    IReadOnlyList<AssistantMessage> Messages,
    IReadOnlyList<AssistantToolSpec> Tools);

/// <summary>What a streaming answer produces, in order.</summary>
public abstract record AssistantStreamEvent;

public sealed record TextDelta(string Text) : AssistantStreamEvent;

/// <summary>A complete tool call (the arguments are already reassembled from the stream).</summary>
public sealed record ToolCallReceived(AssistantToolCall Call) : AssistantStreamEvent;

/// <summary>
/// Talks to one model of one provider. The app depends on this interface, not on an SDK, so the
/// implementation can be replaced (and faked in tests). Failures surface as <see cref="AssistantException"/>.
/// </summary>
public interface IAssistantClient
{
    IAsyncEnumerable<AssistantStreamEvent> StreamAsync(AssistantRequest request, CancellationToken cancellationToken);

    /// <summary>The model ids the provider offers (for the settings screen).</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken);

    /// <summary>One minimal call, to check key, address and model ("Testar conexão"). Throws <see cref="AssistantException"/> when it fails.</summary>
    Task TestAsync(CancellationToken cancellationToken);
}
