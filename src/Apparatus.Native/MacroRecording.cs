using Apparatus.Core;
using Apparatus.Core.Actions;
using Apparatus.Core.Input;

namespace Apparatus.Native;

/// <summary>
/// Records the keyboard into macro steps while it runs. It never blocks a key, except the stop key (Pause), which is
/// swallowed so it does not reach the program being recorded. Nothing is recorded while a classic password box has
/// the focus or while Apparatus itself is in front (so its own windows are not part of the macro).
/// </summary>
public sealed class MacroRecordingSession : IDisposable
{
    public const uint StopKey = 0x13;   // Pause/Break

    private readonly KeyboardHub _hub;
    private readonly string _ownProcess;
    private readonly bool _acceptInjected;
    private readonly MacroRecorder _recorder = new();
    private IDisposable? _subscription;
    private int _stopRaised;
    private int _passwordSkips;

    /// <param name="ownProcess">Keys typed while this process is in front are not recorded (default: the running program).</param>
    /// <param name="acceptInjected">Also record keys sent by programs (SendInput). Only for tests: a person's keys are never "injected".</param>
    public MacroRecordingSession(KeyboardHub hub, string? ownProcess = null, bool acceptInjected = false)
    {
        _hub = hub;
        _ownProcess = ownProcess ?? (Environment.ProcessPath is { } p ? Path.GetFileNameWithoutExtension(p) : "Apparatus");
        _acceptInjected = acceptInjected;
    }

    public bool IsRecording => _subscription is not null;

    public int StepCount => _recorder.StepCount;

    /// <summary>Keys that could not be written as steps.</summary>
    public int SkippedKeys => _recorder.Skipped;

    /// <summary>Key presses ignored because a password box had the focus.</summary>
    public int PasswordKeysIgnored => Volatile.Read(ref _passwordSkips);

    /// <summary>Raised once, on the hook thread, when the user presses the stop key.</summary>
    public event Action? StopRequested;

    public void Start()
    {
        if (_subscription is not null) return;
        _stopRaised = 0;
        _subscription = _hub.Subscribe(OnKey);
    }

    /// <summary>Stops listening and returns the steps recorded so far.</summary>
    public IReadOnlyList<ActionStep> Stop()
    {
        _subscription?.Dispose();
        _subscription = null;
        return _recorder.Steps();
    }

    private bool OnKey(KeyEvent e)
    {
        if (KeyboardHook.IsOwn(e) || (e.IsInjected && !_acceptInjected)) return false;   // only what a person types

        if (e.VirtualKey == StopKey)
        {
            if (e.IsDown && Interlocked.Exchange(ref _stopRaised, 1) == 0) StopRequested?.Invoke();
            return true;
        }

        if (!e.IsDown || KeyTranslator.IsModifierOrLock(e.VirtualKey)) return false;
        if (KeyTranslator.ForegroundProcessName().Equals(_ownProcess, StringComparison.OrdinalIgnoreCase)) return false;
        if (KeyTranslator.IsPasswordFieldFocused())
        {
            Interlocked.Increment(ref _passwordSkips);
            return false;
        }

        long now = Environment.TickCount64;
        switch (KeyTranslator.Classify(e, out string text))
        {
            case KeyAction.Text:
                _recorder.AddText(text, now);
                break;
            case KeyAction.Backspace:
                _recorder.AddBackspace(now);
                break;
            case KeyAction.Reset:
                _recorder.AddCombo(new KeyCombo(HeldModifiers(), e.VirtualKey), now);
                break;
        }
        return false;
    }

    private static HotkeyModifiers HeldModifiers()
    {
        var m = HotkeyModifiers.None;
        if (KeyTranslator.Down(0x11)) m |= HotkeyModifiers.Control;
        if (KeyTranslator.Down(0x12)) m |= HotkeyModifiers.Alt;
        if (KeyTranslator.Down(0x10)) m |= HotkeyModifiers.Shift;
        if (KeyTranslator.Down(0x5B) || KeyTranslator.Down(0x5C)) m |= HotkeyModifiers.Win;
        return m;
    }

    public void Dispose() => _subscription?.Dispose();
}
