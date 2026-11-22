using System.Diagnostics;
using System.Runtime.InteropServices;
using Apparatus.Core;
using Apparatus.Core.Start;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Apparatus.Native;

// ============================================================ registry tweaks

/// <summary>
/// DWORD access under HKCU for <see cref="TweakService"/>. A non-null <paramref name="sandboxRoot"/> redirects every
/// path below that key, so tests never touch the real taskbar settings.
/// </summary>
public sealed class RegistryTweakStore : ITweakRegistry
{
    private readonly string? _sandboxRoot;

    public RegistryTweakStore(string? sandboxRoot = null) => _sandboxRoot = sandboxRoot;

    private string Path(string keyPath) => _sandboxRoot is null ? keyPath : _sandboxRoot + "\\" + keyPath;

    public int? ReadDword(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Path(keyPath));
        return key?.GetValue(valueName) is int v ? v : null;
    }

    public void WriteDword(string keyPath, string valueName, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Path(keyPath), writable: true)
            ?? throw new IOException("Não foi possível abrir a chave do registro.");
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Path(keyPath), writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

// ================================================================== taskbar

public enum TaskbarEdge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

public readonly record struct TaskbarPlacement(TaskbarEdge Edge, int Left, int Top, int Right, int Bottom, bool AutoHide)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public static unsafe class TaskbarInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public nint Hwnd;
        public uint CallbackMessage;
        public uint Edge;
        public Rect Rect;
        public nint LParam;
    }

    private const uint ABM_GETSTATE = 4, ABM_GETTASKBARPOS = 5, ABS_AUTOHIDE = 1;

    [DllImport("shell32.dll")] private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);

    /// <summary>Where the primary taskbar is, or null when the shell does not say.</summary>
    public static TaskbarPlacement? Get()
    {
        var data = new AppBarData { Size = (uint)sizeof(AppBarData) };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == 0) return null;
        var state = new AppBarData { Size = (uint)sizeof(AppBarData) };
        bool autoHide = ((uint)SHAppBarMessage(ABM_GETSTATE, ref state) & ABS_AUTOHIDE) != 0;
        return new TaskbarPlacement((TaskbarEdge)data.Edge, data.Rect.Left, data.Rect.Top, data.Rect.Right, data.Rect.Bottom, autoHide);
    }

    /// <summary>The Windows 11 taskbar can be left- or centre-aligned; the Start button follows (Explorer's TaskbarAl).</summary>
    public static bool IconsAreCentered(ITweakRegistry registry) =>
        (registry.ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAl") ?? 1) != 0;
}

// ============================================================== explorer

public static class ExplorerControl
{
    /// <summary>
    /// Restarts the Windows shell (taskbar and desktop). Windows relaunches it by itself; if that has not
    /// happened after a few seconds it is started here. Open File Explorer windows are closed by this.
    /// </summary>
    public static async Task RestartAsync()
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { p.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { p.Dispose(); }
        }

        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(100).ConfigureAwait(false);
            var running = Process.GetProcessesByName("explorer");
            bool up = running.Length > 0;
            foreach (var p in running) p.Dispose();
            if (up) return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose();
    }
}

// ============================================================ power & session

public enum PowerCommand { Lock, SignOut, Sleep, Hibernate, Restart, Shutdown }

public static class PowerActions
{
    public static void Run(PowerCommand command)
    {
        switch (command)
        {
            case PowerCommand.Lock: SystemCommands.Run("lock"); break;
            case PowerCommand.Sleep: SystemCommands.Run("sleep"); break;
            case PowerCommand.SignOut: Shutdown("/l"); break;
            case PowerCommand.Hibernate: Shutdown("/h"); break;
            case PowerCommand.Restart: Shutdown("/r /t 0"); break;
            case PowerCommand.Shutdown: Shutdown("/s /t 0"); break;
        }
    }

    private static void Shutdown(string args) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
}

// ============================================================== Win key tap

/// <summary>
/// Opens Apparatus' own Start menu when the Windows key is tapped alone, and keeps Windows' Start menu from opening.
/// Every other Win shortcut (Win+E, Win+D, Win+Shift+S...) is untouched: the detector only reacts to a Win press that
/// had no other key or mouse button in between.
/// </summary>
public sealed unsafe class WinKeyInterceptor : IDisposable
{
    private readonly KeyboardHub _hub;
    private readonly Apparatus.Core.Input.WinTapDetector _detector = new();
    private IDisposable? _subscription;

    /// <summary>Raised on the hook thread when a lone tap of the Windows key is detected. Must return quickly.</summary>
    public event Action? Tapped;

    /// <summary>Test aid (APPARATUS_DEBUG_NO_MASK=1): skip the masking key, which is what an update of Windows could break, so the failsafe can be exercised for real.</summary>
    private static readonly bool DebugNoMask = Environment.GetEnvironmentVariable("APPARATUS_DEBUG_NO_MASK") == "1";

    private readonly Func<bool> _canIntercept;

    /// <param name="canIntercept">
    /// Asked on every tap. When it says no (the UI is frozen, or the failsafe gave the menu back to Windows) the tap is left
    /// alone, so Windows opens its own Start menu.
    /// </param>
    public WinKeyInterceptor(KeyboardHub hub, Func<bool>? canIntercept = null)
    {
        _hub = hub;
        _canIntercept = canIntercept ?? (() => true);
    }

    public bool IsActive => _subscription is not null;

    public void SetActive(bool active)
    {
        if (active && _subscription is null)
        {
            _detector.Reset();
            _subscription = _hub.Subscribe(OnKey);
        }
        else if (!active && _subscription is not null)
        {
            _subscription.Dispose();
            _subscription = null;
        }
    }

    private bool OnKey(KeyEvent e)
    {
        if (KeyboardHook.IsOwn(e)) return false;   // our own masking key must not cancel or retrigger anything

        var outcome = _detector.Feed(e.VirtualKey, e.IsDown);
        if (!outcome.Tapped) return false;
        if (!_canIntercept()) return false;   // not hidden from Windows: its own menu opens

        // Windows decides "open Start" on the Win release only if nothing else was pressed meanwhile: press an unassigned
        // key now, before the release reaches it.
        if (outcome.MaskWindowsKey && !DebugNoMask) InjectMask();
        Tapped?.Invoke();
        return false;   // the release itself must still arrive, or the system would think Win is stuck down
    }

    private static void InjectMask()
    {
        var inputs = new INPUT[2];
        for (int i = 0; i < 2; i++)
        {
            inputs[i].type = INPUT_TYPE.INPUT_KEYBOARD;
            inputs[i].Anonymous.ki.wVk = (VIRTUAL_KEY)Apparatus.Core.Input.WinTapDetector.MaskKey;
            inputs[i].Anonymous.ki.dwExtraInfo = KeyboardHook.OwnMarker;
            inputs[i].Anonymous.ki.dwFlags = i == 0 ? 0 : KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
        }
        if (PInvoke.SendInput(inputs, sizeof(INPUT)) != inputs.Length) AppLog.Warn("Masking the Windows key failed; Windows' Start menu may open too");
    }

    public void Dispose() => SetActive(false);
}
