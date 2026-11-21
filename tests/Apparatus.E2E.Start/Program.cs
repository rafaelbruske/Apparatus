// End-to-end checks for the Start menu support.
//
// Default run (safe, no input is injected, nothing outside a throw-away registry key and a temp folder is touched):
//   - the registry-backed tweak store against a sandbox key under HKCU\Software\ApparatusE2eSandbox
//   - the taskbar position query
//   - the shared keyboard hook starting and stopping with its subscribers
//
//   dotnet run --project tests/Apparatus.E2E.Start
//
// With --inject-win <path to Apparatus.exe> <empty temp folder> it also starts Apparatus in an isolated profile with the
// Windows-key feature on, and TAPS THE REAL WINDOWS KEY with SendInput (then Esc). While it runs (about 20 s) do not touch
// the keyboard; if the feature were broken, Windows' own Start menu would flash open. It checks that Apparatus' menu
// opens and Windows' does not.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Apparatus.Core.Start;
using Apparatus.Native;
using Microsoft.Win32;

internal static class Program
{
    private static readonly List<string> Report = new();
    private static int _passed, _failed;

    private static int Main(string[] args)
    {
        const string sandbox = @"Software\ApparatusE2eSandbox";
        string temp = Path.Combine(Path.GetTempPath(), "apparatus-e2e-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            RegistryChecks(sandbox, temp);
            TaskbarChecks();
            HookLifecycleChecks();
            if (args.Length >= 3 && args[0] == "--inject-win") WinTapChecks(args[1], args[2]);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(sandbox, throwOnMissingSubKey: false);
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }

        Console.WriteLine(string.Join("\n", Report));
        Console.WriteLine($"\npassed {_passed}, failed {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------ registry

    private static void RegistryChecks(string sandbox, string temp)
    {
        Registry.CurrentUser.DeleteSubKeyTree(sandbox, throwOnMissingSubKey: false);
        var store = new RegistryTweakStore(sandbox);
        const string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

        Check("an absent value reads as null", store.ReadDword(adv, "TaskbarAl") is null, "");
        store.WriteDword(adv, "TaskbarAl", 0);
        Check("a written DWORD reads back", store.ReadDword(adv, "TaskbarAl") == 0, "");
        using (var key = Registry.CurrentUser.OpenSubKey(sandbox + "\\" + adv))
            Check("it is stored as a REG_DWORD inside the sandbox key", key?.GetValueKind("TaskbarAl") == RegistryValueKind.DWord, "");

        // The real taskbar key must be untouched by the sandbox store: compare before and after.
        int? realBefore = new RegistryTweakStore().ReadDword(adv, "TaskbarAl");
        store.WriteDword(adv, "TaskbarAl", 1);
        Check("the sandbox never touches the real taskbar settings", new RegistryTweakStore().ReadDword(adv, "TaskbarAl") == realBefore, "");

        // The service end to end over the real registry API.
        var svc = new TweakService(store, Path.Combine(temp, "backup.json"));
        store.DeleteValue(adv, "ShowSecondsInSystemClock");
        var applied = svc.Apply("clock-seconds", 1);
        Check("Apply writes through the real registry", applied.Success && store.ReadDword(adv, "ShowSecondsInSystemClock") == 1, applied.Message);
        var restored = svc.Restore("clock-seconds");
        Check("Restore deletes a value that did not exist", restored.Success && store.ReadDword(adv, "ShowSecondsInSystemClock") is null, restored.Message);

        store.WriteDword(adv, "TaskbarGlomLevel", 0);
        store.WriteDword(adv, "MMTaskbarGlomLevel", 1);
        svc.Apply("combine-buttons", 2);
        svc.Restore("combine-buttons");
        Check("Restore puts the original value and its mirror back",
            store.ReadDword(adv, "TaskbarGlomLevel") == 0 && store.ReadDword(adv, "MMTaskbarGlomLevel") == 1, "");

        Registry.CurrentUser.DeleteSubKeyTree(sandbox, throwOnMissingSubKey: false);
        Check("the sandbox is removed afterwards", Registry.CurrentUser.OpenSubKey(sandbox) is null, "");
    }

    // ------------------------------------------------------------- taskbar

    private static void TaskbarChecks()
    {
        var tb = TaskbarInfo.Get();
        Report.Add($"INFO  taskbar: {(tb is { } t ? $"{t.Edge} {t.Width}x{t.Height} at {t.Left},{t.Top} autohide={t.AutoHide}" : "unknown")}");
        Check("the shell reports where the taskbar is", tb is not null, "");
        if (tb is { } p) Check("the reported rectangle is not empty", p.Width > 0 && p.Height > 0, $"{p.Width}x{p.Height}");
    }

    // ---------------------------------------------------------------- hook

    private static void HookLifecycleChecks()
    {
        using var hub = new KeyboardHub();
        Check("the hook is not installed without subscribers", !hub.IsRunning, "");
        var a = hub.Subscribe(_ => false);
        Check("the first subscriber installs the hook", hub.IsRunning && hub.SubscriberCount == 1, "");
        var b = hub.Subscribe(_ => false);
        a.Dispose();
        Check("the hook stays while somebody is subscribed", hub.IsRunning && hub.SubscriberCount == 1, "");
        b.Dispose();
        Check("the last subscriber removes the hook", !hub.IsRunning && hub.SubscriberCount == 0, "");

        var again = hub.Subscribe(_ => false);
        Check("it can be installed again", hub.IsRunning, "");
        again.Dispose();
    }

    // ----------------------------------------------------------- Win tap

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private static string ForegroundProcess()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return "";
        GetWindowThreadProcessId(h, out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName; } catch (ArgumentException) { return ""; }
    }

    private static void Tap(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        keybd_event(vk, 0, 2, UIntPtr.Zero);
    }

    private static string Log(string profile)
    {
        try
        {
            using var fs = new FileStream(Path.Combine(profile, "data", "logs", "apparatus.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(fs).ReadToEnd();
        }
        catch (IOException) { return ""; }
    }

    private static void WinTapChecks(string exe, string profile)
    {
        if (Process.GetProcessesByName("Apparatus").Length > 0) { Report.Add("SKIP  Win tap: an Apparatus instance is already running"); return; }
        if (Directory.Exists(profile)) Directory.Delete(profile, true);
        Directory.CreateDirectory(Path.Combine(profile, "config"));
        File.WriteAllText(Path.Combine(profile, "config", "settings.json"),
            "{\"IndexFiles\":false,\"SnippetExpansion\":false,\"ExtensionSampleInstalled\":true,\"StartMenuEnabled\":true}");

        var psi = new ProcessStartInfo(exe, "--background") { UseShellExecute = false };
        psi.Environment["APPARATUS_PROFILE_DIR"] = profile;
        using var app = Process.Start(psi)!;
        try
        {
            for (int i = 0; i < 100 && !Log(profile).Contains("Tray icon added"); i++) Thread.Sleep(100);
            Thread.Sleep(2500);
            Check("the Windows-key hook is installed when the feature is on", Log(profile).Contains("Keyboard hook installed"), "");

            Tap(0x5B);   // a lone tap of the left Windows key
            Thread.Sleep(1500);
            string front = ForegroundProcess();
            Report.Add($"INFO  foreground after the Win tap: {front}");
            Check("a lone Win tap opens Apparatus' menu", Log(profile).Contains("Start menu shown"), "no 'Start menu shown' in the log");
            Check("and Windows' own Start menu does not open",
                front is not ("StartMenuExperienceHost" or "SearchHost" or "SearchApp"), front);

            Tap(0x1B);   // Esc closes it
            Thread.Sleep(600);
            Check("Esc closes the menu", Log(profile).Contains("Start menu hidden"), "no 'Start menu hidden' in the log");

            Tap(0x5B);
            Thread.Sleep(1200);
            Check("it opens again on the next tap", Regex(Log(profile), "Start menu shown") >= 2, "");
            Tap(0x5B);   // second tap while visible closes it
            Thread.Sleep(800);
            Check("a tap while it is open closes it", Regex(Log(profile), "Start menu hidden") >= 2, "");
        }
        finally
        {
            try { app.Kill(true); app.WaitForExit(3000); } catch (InvalidOperationException) { }
        }
    }

    private static int Regex(string text, string needle)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (ok) _passed++; else _failed++;
        Report.Add($"{(ok ? "PASS" : "FAIL")}  {name}{(ok ? "" : "  — " + detail)}");
    }
}
