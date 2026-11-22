using System.Diagnostics;
using Apparatus.Core;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Apparatus.Native;

/// <summary>The running Windows build, as "build.revision" (for example 26300.9974).</summary>
public static class OsBuild
{
    public static string Current()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string build = key?.GetValue("CurrentBuildNumber") as string ?? key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString();
            string revision = key?.GetValue("UBR") is int ubr ? "." + ubr : "";
            return build + revision;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return Environment.OSVersion.Version.ToString();
        }
    }
}

/// <summary>What the shell says about the foreground: a game in exclusive full screen, a presentation...</summary>
public static class ScreenState
{
    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    public static Apparatus.Core.Start.UserNotificationState Current()
    {
        try
        {
            return SHQueryUserNotificationState(out int state) == 0
                ? (Apparatus.Core.Start.UserNotificationState)state
                : Apparatus.Core.Start.UserNotificationState.AcceptsNotifications;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Apparatus.Core.Start.UserNotificationState.AcceptsNotifications;
        }
    }
}

/// <summary>Knows whether Windows' own Start menu is on screen, and can open it. Used to notice (and undo) a failed replacement.</summary>
public static unsafe class OfficialStart
{
    private static readonly string[] HostProcesses = { "StartMenuExperienceHost", "SearchHost", "SearchApp" };

    // The host processes are few and long-lived; looking them up by name costs a few milliseconds, so it is not repeated while polling.
    private static readonly object HostGate = new();
    private static HashSet<uint>? s_hostPids;
    private static long s_hostPidsAt;

    private static HashSet<uint> HostPids()
    {
        lock (HostGate)
        {
            // (A first-time marker such as long.MinValue would overflow in the subtraction and look "fresh" forever.)
            if (s_hostPids is not null && Environment.TickCount64 - s_hostPidsAt < 5000) return s_hostPids;
            var pids = new HashSet<uint>();
            foreach (var name in HostProcesses)
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    pids.Add((uint)p.Id);
                    p.Dispose();
                }
            }
            s_hostPids = pids;
            s_hostPidsAt = Environment.TickCount64;
            return pids;
        }
    }

    private delegate bool EnumProc(nint hwnd, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    private const int DWMWA_CLOAKED = 14;

    /// <summary>True when a visible window of the shell's Start/Search host exists. When closed, that window is hidden or cloaked.</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetForegroundWindow();

    public static bool IsOpen()
    {
        var hosts = HostPids();
        if (hosts.Count == 0) return false;

        // Windows 11 26300 shows its Start menu as a CoreWindow of SearchHost titled "Pesquisar" that EnumWindows does not list
        // (found by experiment), but that becomes the foreground window while it is open. That is the primary signal.
        nint fg = GetForegroundWindow();
        if (fg != 0 && IsLargeVisibleWindowOf(fg, hosts)) return true;

        // Other builds list it as an ordinary top-level window.
        bool open = false;
        EnumWindows((hwnd, _) =>
        {
            if (!IsLargeVisibleWindowOf(hwnd, hosts)) return true;
            open = true;
            return false;
        }, 0);
        return open;
    }

    private static bool IsLargeVisibleWindowOf(nint hwnd, HashSet<uint> pids)
    {
        if (!IsWindowVisible(hwnd)) return false;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (!pids.Contains(pid)) return false;
        Interop.GetWindowRect(hwnd, out var r);
        return r.Width > 200 && r.Height > 200;
    }

    /// <summary>
    /// Watches for Windows' menu to show up for a while (it can open and be dismissed again within a few hundred milliseconds when
    /// another window takes the focus). True as soon as it is seen.
    /// </summary>
    public static bool OpensWithin(TimeSpan window, TimeSpan every)
    {
        long end = Environment.TickCount64 + (long)window.TotalMilliseconds;
        do
        {
            if (IsOpen()) return true;
            Thread.Sleep(every);
        }
        while (Environment.TickCount64 < end);
        return false;
    }

    /// <summary>Opens Windows' Start menu with Ctrl+Esc (marked as ours so the keyboard hooks ignore it).</summary>
    public static void Open()
    {
        var inputs = new INPUT[4];
        ushort[] keys = { 0x11, 0x1B, 0x1B, 0x11 };
        for (int i = 0; i < 4; i++)
        {
            inputs[i].type = INPUT_TYPE.INPUT_KEYBOARD;
            inputs[i].Anonymous.ki.wVk = (VIRTUAL_KEY)keys[i];
            inputs[i].Anonymous.ki.dwExtraInfo = KeyboardHook.OwnMarker;
            inputs[i].Anonymous.ki.dwFlags = i >= 2 ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0;
        }
        if (PInvoke.SendInput(inputs, sizeof(INPUT)) != inputs.Length) AppLog.Warn("Opening Windows' Start menu (Ctrl+Esc) failed");
    }
}
