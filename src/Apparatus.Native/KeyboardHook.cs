using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apparatus.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Apparatus.Native;

public readonly record struct KeyEvent(uint VirtualKey, uint ScanCode, bool IsDown, bool IsExtended, bool IsInjected, nuint ExtraInfo);

/// <summary>
/// System-wide low-level keyboard hook on its own thread with its own message loop, so a slow UI thread
/// can never make Windows time the hook out. The handler runs on the hook thread and must return quickly;
/// returning true swallows the key.
/// </summary>
public sealed unsafe class KeyboardHook : IDisposable
{
    /// <summary>
    /// dwExtraInfo on every key Apparatus injects, so it can recognise (and ignore) its own input.
    /// Kept below 0x80000000: a larger constant is sign-extended to 64 bits when stored, while the system hands
    /// the hook only the low 32 bits, and the two would never compare equal.
    /// </summary>
    public const nuint OwnMarker = 0x4A99A510;

    /// <summary>True for keys that Apparatus injected itself. Compares the low 32 bits, which is all the hook receives.</summary>
    public static bool IsOwn(in KeyEvent e) => (uint)e.ExtraInfo == (uint)OwnMarker;

    private static KeyboardHook? s_instance;

    private Thread? _thread;
    private uint _threadId;
    private readonly ManualResetEventSlim _started = new();

    public Func<KeyEvent, bool>? Handler { get; set; }

    public bool IsRunning => _thread is { IsAlive: true };

    public void Start()
    {
        if (IsRunning) return;
        s_instance = this;
        _started.Reset();
        _thread = new Thread(Run) { IsBackground = true, Name = "Apparatus keyboard hook" };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(2));
    }

    public void Stop()
    {
        if (_thread is null) return;
        if (_threadId != 0) PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(1));
        _thread = null;
        _threadId = 0;
    }

    private void Run()
    {
        _threadId = PInvoke.GetCurrentThreadId();
        using var hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &HookProc, null, 0);
        if (hook.IsInvalid)
        {
            AppLog.Error($"Installing the keyboard hook failed (error {Marshal.GetLastWin32Error()})");
            _started.Set();
            return;
        }
        AppLog.Info("Keyboard hook installed");
        _started.Set();

        while (PInvoke.GetMessage(out var msg, HWND.Null, 0, 0))
        {
            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }
        AppLog.Info("Keyboard hook removed");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static LRESULT HookProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0 && s_instance?.Handler is { } handler)
        {
            try
            {
                var k = (KBDLLHOOKSTRUCT*)lParam.Value;
                uint message = (uint)wParam.Value;
                bool down = message is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN;
                var e = new KeyEvent(
                    k->vkCode, k->scanCode, down,
                    (k->flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_EXTENDED) != 0,
                    (k->flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_INJECTED) != 0,
                    k->dwExtraInfo);
                if (handler(e)) return new LRESULT(1);
            }
            catch (Exception ex)
            {
                // A throwing hook must never block the user's keyboard.
                AppLog.Error("Keyboard hook handler failed", ex);
            }
        }
        return PInvoke.CallNextHookEx(null, code, wParam, lParam);
    }

    public void Dispose() => Stop();
}
