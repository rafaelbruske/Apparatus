using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Apparatus.Core.Assistant;

public enum ChatEntryRole
{
    User,
    Assistant,
    /// <summary>A short remark from the app (for example "this model only answers with text").</summary>
    Notice,
    /// <summary>Something went wrong; the text is already worded for the user.</summary>
    Error,
    /// <summary>A button the assistant offers (see <see cref="ChatEntry.Suggestion"/>). It does nothing until the user picks it.</summary>
    Suggestion,
}

/// <summary>One line of the conversation as the screen shows it. The text of an answer grows while it streams.</summary>
public sealed class ChatEntry : INotifyPropertyChanged
{
    private string _text;
    private bool _isSelected;

    public ChatEntry(ChatEntryRole role, string text = "")
    {
        Role = role;
        _text = text;
    }

    /// <summary>A suggestion card: the validated action behind the button.</summary>
    public ChatEntry(SuggestedAction suggestion) : this(ChatEntryRole.Suggestion, suggestion.Title)
    {
        Suggestion = suggestion;
    }

    public ChatEntryRole Role { get; }

    public SuggestedAction? Suggestion { get; }

    /// <summary>Second line of a card (what it opens or runs).</summary>
    public string Detail => Suggestion?.Description ?? "";

    /// <summary>True for the card that Enter would pick (the box must be empty).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
        }
    }

    /// <summary>What the screen shows: the text, or "Pensando…" while an answer has not started to arrive.</summary>
    public string Display => Role == ChatEntryRole.Assistant && _text.Length == 0 ? "Pensando…" : _text;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum AskOutcome
{
    /// <summary>The answer is complete.</summary>
    Answered,
    /// <summary>The user cancelled; the question is not part of the conversation and should go back in the box.</summary>
    Cancelled,
    /// <summary>It failed (an error line was added); the question should go back in the box.</summary>
    Failed,
    /// <summary>Nothing was sent (blank question, or another answer is still coming).</summary>
    Ignored,
}

/// <summary>
/// What the chat screen shows, and the one operation it needs: ask. Keeps the entries (for the screen), drives the engine
/// (for the model) and decides what a failure or a cancellation leaves behind. Memory only; nothing is written to disk.
/// </summary>
public sealed class AssistantChat
{
    private readonly Func<AssistantEngine> _engineFactory;
    private CancellationTokenSource? _current;

    /// <param name="engineFactory">Builds an engine from the current settings; throws <see cref="AssistantException"/> (with a message for the user) when something is missing.</param>
    public AssistantChat(Func<AssistantEngine> engineFactory, AssistantSession session)
    {
        _engineFactory = engineFactory;
        Session = session;
    }

    public AssistantSession Session { get; }

    public ObservableCollection<ChatEntry> Entries { get; } = new();

    public bool IsBusy { get; private set; }

    public event EventHandler? BusyChanged;

    /// <summary>Sends a question and fills <see cref="Entries"/> as the answer arrives. Never throws for provider problems: they become an error entry.</summary>
    public async Task<AskOutcome> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        question = question.Trim();
        if (question.Length == 0 || IsBusy) return AskOutcome.Ignored;

        ExpireIfIdle();
        // Errors were seen already, and the buttons of an earlier answer are stale (Enter always means the newest ones).
        foreach (var old in Entries.Where(e => e.Role is ChatEntryRole.Error or ChatEntryRole.Suggestion).ToList()) Entries.Remove(old);

        var user = new ChatEntry(ChatEntryRole.User, question);
        var answer = new ChatEntry(ChatEntryRole.Assistant);
        Entries.Add(user);
        Entries.Add(answer);

        _current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetBusy(true);
        try
        {
            var engine = _engineFactory();
            int offered = 0, rejected = 0;
            await foreach (var e in engine.AskAsync(question, _current.Token))
            {
                switch (e)
                {
                    case AnswerText t:
                        answer.Text += t.Delta;
                        break;
                    case AssistantNotice n:
                        Entries.Add(new ChatEntry(ChatEntryRole.Notice, n.Text));
                        break;
                    case SuggestionOffered s:
                        Entries.Add(new ChatEntry(s.Action) { IsSelected = ++offered == 1 });
                        break;
                    case SuggestionRejected r:
                        rejected++;
                        AppLog.Info($"The assistant's '{r.ToolName}' suggestion was dropped: {r.Reason}");
                        break;
                }
            }

            if (answer.Text.Length == 0)
            {
                Entries.Remove(answer);
                if (offered == 0)
                {
                    Entries.Remove(user);
                    // The engine explained an empty answer in a notice; when it only made calls that were thrown away, say so here.
                    if (rejected > 0)
                        Entries.Add(new ChatEntry(ChatEntryRole.Notice, "O assistente sugeriu algo que o Apparatus não permite e foi descartado. Tente reformular a pergunta."));
                    return AskOutcome.Failed;
                }
                // Only buttons, no text: the question stays and the buttons are the answer.
            }
            return AskOutcome.Answered;
        }
        catch (OperationCanceledException)
        {
            RemoveTurn(user, answer);
            return AskOutcome.Cancelled;
        }
        catch (AssistantException ex)
        {
            return Fail(user, answer, ex.Message);
        }
        catch (Exception ex)
        {
            AppLog.Error("The assistant failed unexpectedly", ex);
            return Fail(user, answer, "Algo deu errado ao falar com o assistente. Veja o registro de erros para detalhes.");
        }
        finally
        {
            _current.Dispose();
            _current = null;
            SetBusy(false);
        }
    }

    private AskOutcome Fail(ChatEntry user, ChatEntry answer, string message)
    {
        RemoveTurn(user, answer);
        Entries.Add(new ChatEntry(ChatEntryRole.Error, message));
        return AskOutcome.Failed;
    }

    /// <summary>Takes an unanswered question out of the screen, together with any button it had already produced.</summary>
    private void RemoveTurn(ChatEntry user, ChatEntry answer)
    {
        Entries.Remove(user);
        Entries.Remove(answer);
        foreach (var s in Suggestions.ToList()) Entries.Remove(s);
    }

    // ------------------------------------------------------------------------------------------ buttons

    /// <summary>The buttons of the latest answer, in order.</summary>
    public IReadOnlyList<ChatEntry> Suggestions => Entries.Where(e => e.Role == ChatEntryRole.Suggestion).ToList();

    /// <summary>The button Enter would pick (the box must be empty), or null when there are none.</summary>
    public ChatEntry? SelectedSuggestion => Suggestions.FirstOrDefault(s => s.IsSelected) ?? Suggestions.FirstOrDefault();

    /// <summary>Moves the selection by <paramref name="delta"/> buttons, wrapping around. Returns the new selection (null when there are none).</summary>
    public ChatEntry? MoveSuggestion(int delta)
    {
        var all = Suggestions;
        if (all.Count == 0) return null;
        int at = Math.Max(0, all.ToList().FindIndex(s => s.IsSelected));
        int next = ((at + delta) % all.Count + all.Count) % all.Count;
        for (int i = 0; i < all.Count; i++) all[i].IsSelected = i == next;
        return all[next];
    }

    /// <summary>Makes <paramref name="entry"/> the selected button (a click on it).</summary>
    public void SelectSuggestion(ChatEntry entry)
    {
        foreach (var s in Suggestions) s.IsSelected = ReferenceEquals(s, entry);
    }

    /// <summary>Stops the answer in progress, if any.</summary>
    public void CancelCurrent()
    {
        try { _current?.Cancel(); }
        catch (ObjectDisposedException) { /* it just finished */ }
    }

    /// <summary>Starts over: stops any answer, forgets the conversation.</summary>
    public void Reset()
    {
        CancelCurrent();
        Entries.Clear();
        Session.Clear();
    }

    /// <summary>Forgets the conversation when it has been idle for too long (the session decides how long).</summary>
    public void ExpireIfIdle()
    {
        if (IsBusy || !Session.IsExpired) return;
        Entries.Clear();
        Session.Clear();
    }

    private void SetBusy(bool busy)
    {
        if (IsBusy == busy) return;
        IsBusy = busy;
        BusyChanged?.Invoke(this, EventArgs.Empty);
    }
}
