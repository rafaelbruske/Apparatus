using System.Text;

namespace Apparatus.Core.Snippets;

/// <summary>
/// Follows what the user types and reports the moment the text ends with a snippet trigger.
/// Platform-neutral: the keyboard hook turns key presses into <see cref="Append"/>, <see cref="Backspace"/>
/// and <see cref="Reset"/> calls. Not thread-safe; use it from one thread.
/// </summary>
public sealed class TriggerBuffer
{
    private const int MaxBuffer = 64;

    private readonly StringBuilder _typed = new();
    private volatile Dictionary<char, List<Snippet>> _byLastChar = new();

    public TriggerBuffer(IEnumerable<Snippet>? snippets = null) => SetSnippets(snippets ?? Array.Empty<Snippet>());

    /// <summary>
    /// Replaces the set of expandable snippets (only those with a trigger and AutoExpand). Safe to call from any
    /// thread while another thread is typing into the buffer: the lookup table is swapped as a whole.
    /// </summary>
    public void SetSnippets(IEnumerable<Snippet> snippets)
    {
        var map = new Dictionary<char, List<Snippet>>();
        foreach (var s in snippets)
        {
            if (!s.AutoExpand || s.Trigger.Length < SnippetStore.MinTriggerLength) continue;
            char last = s.Trigger[^1];
            if (!map.TryGetValue(last, out var list)) map[last] = list = new List<Snippet>();
            list.Add(s);
        }
        _byLastChar = map;
    }

    public string Current => _typed.ToString();

    /// <summary>Forget what was typed (focus change, cursor movement, shortcuts, Enter/Tab...).</summary>
    public void Reset() => _typed.Clear();

    public void Backspace()
    {
        if (_typed.Length > 0) _typed.Length--;
    }

    /// <summary>Adds typed text; returns the snippet whose trigger the text now ends with, if any.</summary>
    public Snippet? Append(string text)
    {
        if (text.Length == 0) return null;
        _typed.Append(text);
        if (_typed.Length > MaxBuffer) _typed.Remove(0, _typed.Length - MaxBuffer);

        if (!_byLastChar.TryGetValue(_typed[^1], out var candidates)) return null;
        foreach (var s in candidates)
        {
            if (EndsWithTrigger(s.Trigger))
            {
                // The trigger is consumed by the expansion; start clean afterwards.
                _typed.Clear();
                return s;
            }
        }
        return null;
    }

    private bool EndsWithTrigger(string trigger)
    {
        int start = _typed.Length - trigger.Length;
        if (start < 0) return false;
        for (int i = 0; i < trigger.Length; i++)
        {
            if (_typed[start + i] != trigger[i]) return false;
        }
        // A trigger that begins like a word ("btw") must not fire inside another word ("abtw"); symbol-led
        // triggers (";btw") can fire anywhere. Unknown history (start == 0) counts as a boundary.
        if (char.IsLetterOrDigit(trigger[0]) && start > 0 && char.IsLetterOrDigit(_typed[start - 1])) return false;
        return true;
    }
}
