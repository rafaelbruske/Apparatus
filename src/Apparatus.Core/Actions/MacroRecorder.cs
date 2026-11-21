using System.Text;
using Apparatus.Core.Input;

namespace Apparatus.Core.Actions;

/// <summary>
/// Turns what the user typed into macro steps: runs of typed characters become one "Digitar texto" step, shortcuts and
/// special keys (Enter, Tab, arrows, Ctrl+C...) become "Enviar teclas" steps (consecutive ones are merged), and pauses
/// become "Aguardar" steps. The recorder has no Windows dependencies: the caller classifies each key and passes the time.
/// </summary>
public sealed class MacroRecorder
{
    /// <summary>A silence longer than this is recorded as a wait.</summary>
    public const int WaitThresholdMs = 700;

    public const int MaxWaitMs = 30_000;

    private readonly object _gate = new();
    private readonly List<ActionStep> _steps = new();
    private readonly StringBuilder _text = new();   // raw characters of the open "Digitar texto" step, if any
    private long _lastEventMs = -1;

    /// <summary>Keys that cannot be written as a step (no name the "Enviar teclas" step understands).</summary>
    public int Skipped { get; private set; }

    public int StepCount
    {
        get { lock (_gate) return _steps.Count; }
    }

    public bool IsEmpty => StepCount == 0;

    public void AddText(string text, long nowMs)
    {
        if (text.Length == 0) return;
        lock (_gate)
        {
            BreakIfPaused(nowMs);
            if (!TextIsOpen)
            {
                _steps.Add(ActionStep.Create(StepKind.TypeText, ("text", "")));
                _text.Clear();
            }
            _text.Append(text);
            _steps[^1].Args["text"] = Escape(_text.ToString());
        }
    }

    /// <summary>Backspace erases the character just typed when there is one, otherwise it is a key press like any other.</summary>
    public void AddBackspace(long nowMs)
    {
        lock (_gate)
        {
            if (TextIsOpen && _text.Length > 0 && !GapIsPause(nowMs))
            {
                _text.Length--;
                _lastEventMs = nowMs;
                if (_text.Length == 0) _steps.RemoveAt(_steps.Count - 1);
                else _steps[^1].Args["text"] = Escape(_text.ToString());
                return;
            }
        }
        AddCombo(new KeyCombo(HotkeyModifiers.None, 0x08), nowMs);
    }

    public void AddCombo(KeyCombo combo, long nowMs)
    {
        string name = combo.ToString();
        lock (_gate)
        {
            // Only keys the "Enviar teclas" step can name; anything else would make the macro fail validation.
            if (!KeyCombo.TryParse(name, out _))
            {
                Skipped++;
                return;
            }

            BreakIfPaused(nowMs);
            if (_steps.Count > 0 && _steps[^1].Kind == StepKind.SendKeys && !TextIsOpen)
            {
                _steps[^1].Args["keys"] += " " + name;
            }
            else
            {
                CloseText();
                _steps.Add(ActionStep.Create(StepKind.SendKeys, ("keys", name)));
            }
            _lastEventMs = nowMs;
        }
    }

    /// <summary>The recorded steps (copies, safe to keep).</summary>
    public IReadOnlyList<ActionStep> Steps()
    {
        lock (_gate) return _steps.Select(s => s.Clone()).ToList();
    }

    // -------------------------------------------------------------- internals

    private bool TextIsOpen => _text.Length > 0 && _steps.Count > 0 && _steps[^1].Kind == StepKind.TypeText;

    private void CloseText() => _text.Clear();

    private bool GapIsPause(long nowMs) => _lastEventMs >= 0 && nowMs - _lastEventMs > WaitThresholdMs;

    /// <summary>Starts a new step after a pause, with a wait in between. Also records the time of this event.</summary>
    private void BreakIfPaused(long nowMs)
    {
        if (GapIsPause(nowMs))
        {
            int ms = (int)Math.Min(MaxWaitMs, (nowMs - _lastEventMs + 25) / 50 * 50);
            CloseText();
            _steps.Add(ActionStep.Create(StepKind.Wait, ("ms", ms.ToString())));
        }
        _lastEventMs = nowMs;
    }

    /// <summary>"Digitar texto" treats { and } as template markers; doubling them types the literal character.</summary>
    public static string Escape(string text) => text.Replace("{", "{{").Replace("}", "}}");
}
