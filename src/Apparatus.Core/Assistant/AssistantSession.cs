namespace Apparatus.Core.Assistant;

/// <summary>
/// The conversation, in memory only (nothing is written to disk). Keeps the history valid for the API: every tool
/// call of an assistant message is followed by a "tool" message, even though the call was only a suggestion.
/// Old turns are dropped whole, so a tool message never loses the assistant message it answers.
/// </summary>
public sealed class AssistantSession
{
    private readonly List<AssistantMessage> _messages = new();
    private readonly int _maxMessages;
    private readonly int _maxChars;
    private readonly TimeSpan _idleTimeout;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private DateTime _lastActivity;

    public AssistantSession(int maxMessages = 40, int maxChars = 60_000, TimeSpan? idleTimeout = null, Func<DateTime>? clock = null)
    {
        _maxMessages = Math.Max(2, maxMessages);
        _maxChars = Math.Max(1_000, maxChars);
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(15);
        _clock = clock ?? (() => DateTime.UtcNow);
        _lastActivity = _clock();
    }

    /// <summary>A copy of the history, safe to enumerate while the session changes.</summary>
    public IReadOnlyList<AssistantMessage> Messages
    {
        get { lock (_gate) return _messages.ToList(); }
    }

    public DateTime LastActivityUtc
    {
        get { lock (_gate) return _lastActivity; }
    }

    /// <summary>True when nothing happened for longer than the idle limit; the caller then clears it.</summary>
    public bool IsExpired
    {
        get { lock (_gate) return _messages.Count > 0 && _clock() - _lastActivity > _idleTimeout; }
    }

    public void AddUser(string text)
    {
        lock (_gate)
        {
            _messages.Add(AssistantMessage.User(text));
            Touch();
            Trim();
        }
    }

    /// <summary>
    /// Adds the assistant's answer and, right after it, one "tool" message per call with the given result text
    /// (<see cref="AssistantTools.SuggestedResult"/> for an accepted suggestion, the reason for a rejected one).
    /// An answer with no text and no calls is not added.
    /// </summary>
    public void AddAssistant(string text, IReadOnlyList<(AssistantToolCall Call, string Result)>? calls = null)
    {
        calls ??= Array.Empty<(AssistantToolCall, string)>();
        if (text.Length == 0 && calls.Count == 0) return;
        lock (_gate)
        {
            _messages.Add(AssistantMessage.Assistant(text, calls.Select(c => c.Call).ToList()));
            foreach (var (call, result) in calls) _messages.Add(AssistantMessage.ToolResult(call.Id, result));
            Touch();
            Trim();
        }
    }

    /// <summary>Removes a trailing user message that got no answer (the request failed or was cancelled), so a retry does not repeat it.</summary>
    public void RollBackPendingUser()
    {
        lock (_gate)
        {
            if (_messages.Count > 0 && _messages[^1].Role == AssistantRole.User) _messages.RemoveAt(_messages.Count - 1);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _messages.Clear();
            Touch();
        }
    }

    private void Touch() => _lastActivity = _clock();

    /// <summary>Drops the oldest turns (a user message and what follows it, up to the next user message) while the history is too big. The latest turn always stays.</summary>
    private void Trim()
    {
        while (_messages.Count > 1 && (_messages.Count > _maxMessages || Size() > _maxChars))
        {
            int next = _messages.FindIndex(1, m => m.Role == AssistantRole.User);
            if (next < 0) break;   // only one turn left
            _messages.RemoveRange(0, next);
        }
    }

    private int Size()
    {
        int total = 0;
        foreach (var m in _messages)
        {
            total += m.Text.Length;
            if (m.ToolCalls is not null) foreach (var c in m.ToolCalls) total += c.ArgumentsJson.Length + c.Name.Length;
        }
        return total;
    }
}
