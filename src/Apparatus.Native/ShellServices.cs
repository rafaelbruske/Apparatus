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
    private const string MutexName = @"Local\Apparatus.SingleInstance";
    private const string SignalName = @"Local\Apparatus.ShowLauncher";

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

    public void Dispose()
    {
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

    public static void BringToForeground(nint hwnd) => PInvoke.SetForegroundWindow(new HWND(hwnd));
}
