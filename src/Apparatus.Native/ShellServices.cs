using System.Diagnostics;
using System.Runtime.InteropServices;
using Apparatus.Core;
using Apparatus.Core.Search.Providers;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace Apparatus.Native;

// ======================================================= installed apps

/// <summary>Lists installed applications (classic and packaged) through the shell's AppsFolder.</summary>
public sealed class ShellAppCatalog : IAppCatalog
{
    public Task<IReadOnlyList<AppEntry>> LoadAsync(CancellationToken ct)
    {
        // Shell.Application is an STA COM object; give it its own STA thread.
        var tcs = new TaskCompletionSource<IReadOnlyList<AppEntry>>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(Enumerate(ct)); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "AppsFolder enumeration" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static IReadOnlyList<AppEntry> Enumerate(CancellationToken ct)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Shell.Application unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic folder = shell.NameSpace("shell:AppsFolder");
        var apps = new List<AppEntry>();
        foreach (dynamic item in folder.Items())
        {
            ct.ThrowIfCancellationRequested();
            string name = item.Name;
            string id = item.Path;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id) || IsNoise(name)) continue;
            // Classic apps without an AUMID expose a real path; everything else is addressed through AppsFolder.
            string parsing = Path.IsPathRooted(id) ? id : $"shell:AppsFolder\\{id}";
            apps.Add(new AppEntry(id, name, parsing));
        }
        return apps;
    }

    private static bool IsNoise(string name) =>
        name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Desinstalar", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(" Uninstall", StringComparison.OrdinalIgnoreCase);
}

// ================================================================ launch

public static class ShellLauncher
{
    /// <summary>Opens a file, folder, URL, ms-settings: URI, shell: path or program, like Win+R would.</summary>
    public static void Open(string target)
    {
        var (file, args) = Split(target);
        var psi = new ProcessStartInfo(file) { UseShellExecute = true };
        if (args is not null) psi.Arguments = args;
        Process.Start(psi)?.Dispose();
    }

    public static void RevealInExplorer(string path)
    {
        if (Directory.Exists(path)) Open(path);
        else Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    /// <summary>"rundll32.exe sysdm.cpl,EditEnvironmentVariables" -> program plus arguments.</summary>
    internal static (string File, string? Args) Split(string target)
    {
        int space = target.IndexOf(' ');
        if (space > 0 && target[..space].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(target) && !Directory.Exists(target))
        {
            return (target[..space], target[(space + 1)..]);
        }
        return (target, null);
    }
}

// ============================================================== autostart

public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Apparatus";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>True for a copy installed by the installer (the updater, Update.exe, sits in the folder above the program).</summary>
    public static bool IsInstalledBuild()
    {
        string? exe = Environment.ProcessPath;
        string? parent = exe is null ? null : Directory.GetParent(Path.GetDirectoryName(exe)!)?.FullName;
        return parent is not null && File.Exists(Path.Combine(parent, "Update.exe"));
    }

    /// <summary>
    /// Makes the start-up entry match the setting, for an installed copy only (a build run from a folder must not overwrite it with its own
    /// path). The entry can be missing or point at an old place even though the setting is on, for example after a reinstall.
    /// Returns true when it had to be rewritten.
    /// </summary>
    public static bool Reconcile(bool wanted)
    {
        if (!wanted || !IsInstalledBuild()) return false;
        string expected = $"\"{Environment.ProcessPath}\" --background";
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is string current && string.Equals(current, expected, StringComparison.OrdinalIgnoreCase)) return false;
        Set(true);
        return true;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path");
            key.SetValue(ValueName, $"\"{exe}\" --background");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

// ========================================================= single instance

/// <summary>Only one Apparatus runs; a second launch just asks the first one to show the launcher.</summary>
public sealed class SingleInstance : IDisposable
{
    // A copy started with its own profile folder (APPARATUS_PROFILE_DIR: tests, portable use) is a separate instance: it must not
    // hand over to, or be mistaken for, the copy that runs with the user's normal profile.
    private static readonly string Suffix = Environment.GetEnvironmentVariable("APPARATUS_PROFILE_DIR") is { Length: > 0 } dir
        ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dir.ToLowerInvariant())))[..12]
        : "";

    private static readonly string MutexName = @"Local\Apparatus.SingleInstance" + Suffix;
    private static readonly string SignalName = @"Local\Apparatus.ShowLauncher" + Suffix;

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex, EventWaitHandle signal, bool isFirst)
    {
        _mutex = mutex;
        _signal = signal;
        IsFirst = isFirst;
    }

    public bool IsFirst { get; }

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        return new SingleInstance(mutex, signal, created);
    }

    /// <summary>Called by a second instance before it exits.</summary>
    public void SignalFirstInstance() => _signal.Set();

    /// <summary>The first instance runs <paramref name="onSignal"/> (on a pool thread) when another launch happens.</summary>
    public void Listen(Action onSignal)
    {
        var token = _cts.Token;
        var thread = new Thread(() =>
        {
            var handles = new[] { _signal, token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                try { onSignal(); } catch (Exception ex) { AppLog.Error("Single-instance handler failed", ex); }
            }
        }) { IsBackground = true, Name = "Single instance listener" };
        thread.Start();
    }

    private int _disposed;

    public void Dispose()
    {
        // Called both when the app quits and when Main ends: the second call must do nothing (it used to throw ObjectDisposedException).
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        if (IsFirst) { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } }
        _mutex.Dispose();
        _signal.Dispose();
        _cts.Dispose();
    }
}

// ============================================================ window utils

public static class WindowHelpers
{
    public static (int X, int Y) CursorPosition()
    {
        PInvoke.GetCursorPos(out var pt);
        return (pt.X, pt.Y);
    }

    public static double GetScale(nint hwnd)
    {
        uint dpi = PInvoke.GetDpiForWindow(new HWND(hwnd));
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    public static unsafe void UseRoundedCorners(nint hwnd)
    {
        var pref = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        PInvoke.DwmSetWindowAttribute(new HWND(hwnd), DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &pref, sizeof(DWM_WINDOW_CORNER_PREFERENCE));
    }

    /// <summary>
    /// Brings a window to the front even when this process did not receive the last input. Windows only lets the
    /// process that owns the foreground window (or received the last keystroke) call SetForegroundWindow; attaching
    /// our input queue to the foreground thread's lifts that restriction for the duration of the call.
    /// </summary>
    public static unsafe void BringToForeground(nint hwnd)
    {
        var target = new HWND(hwnd);
        var current = PInvoke.GetForegroundWindow();
        if (current == target) return;

        uint currentThread = current == HWND.Null ? 0 : PInvoke.GetWindowThreadProcessId(current, null);
        uint thisThread = PInvoke.GetCurrentThreadId();
        bool attached = currentThread != 0 && currentThread != thisThread && PInvoke.AttachThreadInput(thisThread, currentThread, true);
        try
        {
            PInvoke.BringWindowToTop(target);
            PInvoke.SetForegroundWindow(target);
        }
        finally
        {
            if (attached) PInvoke.AttachThreadInput(thisThread, currentThread, false);
        }
    }
}
