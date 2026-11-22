using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apparatus.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Apparatus.Native;

public readonly record struct MouseButtonEvent(bool IsDown, int X, int Y, bool IsInjected);

/// <summary>
/// System-wide low-level hook for the left mouse button, on its own thread. Mouse movement never reaches the handler (it is
/// let through right here), so the hook costs almost nothing while the pointer moves. The handler must return quickly;
/// returning true swallows the event.
/// </summary>
public sealed unsafe class MouseHook : IDisposable
{
    private static MouseHook? s_instance;

    private Thread? _thread;
    private uint _threadId;
    private readonly ManualResetEventSlim _started = new();

    public Func<MouseButtonEvent, bool>? Handler { get; set; }

    public bool IsRunning => _thread is { IsAlive: true };

    public void Start()
    {
        if (IsRunning) return;
        s_instance = this;
        _started.Reset();
        _thread = new Thread(Run) { IsBackground = true, Name = "Apparatus mouse hook" };
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
        if (ReferenceEquals(s_instance, this)) s_instance = null;
    }

    private void Run()
    {
        _threadId = PInvoke.GetCurrentThreadId();
        using var hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, &HookProc, null, 0);
        if (hook.IsInvalid)
        {
            AppLog.Error($"Installing the mouse hook failed (error {Marshal.GetLastWin32Error()})");
            _started.Set();
            return;
        }
        AppLog.Info("Mouse hook installed");
        _started.Set();

        while (PInvoke.GetMessage(out var msg, HWND.Null, 0, 0))
        {
            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }
        AppLog.Info("Mouse hook removed");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static LRESULT HookProc(int code, WPARAM wParam, LPARAM lParam)
    {
        uint message = (uint)wParam.Value;
        if (code >= 0 && message is PInvoke.WM_LBUTTONDOWN or PInvoke.WM_LBUTTONUP && s_instance?.Handler is { } handler)
        {
            try
            {
                var m = (MSLLHOOKSTRUCT*)lParam.Value;
                bool injected = (m->flags & 0x1) != 0;   // LLMHF_INJECTED
                if (handler(new MouseButtonEvent(message == PInvoke.WM_LBUTTONDOWN, m->pt.X, m->pt.Y, injected))) return new LRESULT(1);
            }
            catch (Exception ex)
            {
                // A throwing hook must never block the user's mouse.
                AppLog.Error("Mouse hook handler failed", ex);
            }
        }
        return PInvoke.CallNextHookEx(null, code, wParam, lParam);
    }

    public void Dispose() => Stop();
}
