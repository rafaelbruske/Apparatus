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

    [STAThread]
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
            if (args.Length >= 3 && args[0] == "--sync") SyncChecks(args[1], args[2]);
            if (args.Length >= 1 && args[0] == "--record") RecordChecks();
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

    // --------------------------------------------------- macro recording

    /// <summary>
    /// Records keys typed into a form of this process (only while it is verifiably in front): "ola", a pause, Ctrl+A, Enter, then
    /// Pause to stop. Takes about 6 seconds and presses real keys, so do not use the keyboard meanwhile.
    /// </summary>
    private static void RecordChecks()
    {
        using var hub = new KeyboardHub();
        using var session = new MacroRecordingSession(hub, ownProcess: "no-such-process", acceptInjected: true);
        int stopped = 0;
        session.StopRequested += () => Interlocked.Increment(ref stopped);

        using var form = new System.Windows.Forms.Form { Text = "record target", Width = 400, Height = 200, StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen, TopMost = true };
        var box = new System.Windows.Forms.TextBox { Multiline = true, Dock = System.Windows.Forms.DockStyle.Fill };
        form.Controls.Add(box);
        form.Shown += (_, _) => { form.Activate(); box.Focus(); };

        bool injected = false;
        var worker = new Thread(() =>
        {
            Thread.Sleep(1200);
            bool front = false;
            for (int i = 0; i < 20 && !front; i++)
            {
                form.Invoke(() => { form.Activate(); front = GetForegroundWindow() == form.Handle; });
                if (!front) Thread.Sleep(150);
            }
            if (!front) { form.Invoke(form.Close); return; }

            session.Start();
            Thread.Sleep(300);
            injected = true;
            foreach (byte vk in new byte[] { 0x4F, 0x4C, 0x41 }) { Tap(vk); Thread.Sleep(60); }   // o l a
            Thread.Sleep(1300);
            keybd_event(0x11, 0, 0, UIntPtr.Zero);                                              // Ctrl+A
            Tap(0x41);
            keybd_event(0x11, 0, 2, UIntPtr.Zero);
            Thread.Sleep(80);
            Tap(0x0D);                                                                            // Enter
            Thread.Sleep(100);
            Tap(0x13);                                                                            // Pause: stop
            Thread.Sleep(400);
            form.Invoke(form.Close);
        });
        worker.Start();
        System.Windows.Forms.Application.Run(form);
        worker.Join();

        if (!injected) { Report.Add("SKIP  macro recording: the target form could not take the foreground"); return; }
        var steps = session.Stop();
        var text = steps.Select(Apparatus.Core.Actions.StepCatalog.Summarize).ToList();
        Report.Add("INFO  recorded: " + string.Join(" | ", text));
        Check("Pause was seen as the stop key", stopped == 1, $"stop raised {stopped} time(s)");
        Check("typed letters become one text step", text.Count > 0 && text[0] == "Digitar: ola", text.FirstOrDefault() ?? "(none)");
        Check("the pause between them becomes a wait", text.Count > 1 && text[1].StartsWith("Aguardar 1") && text[1].EndsWith(" ms"), text.ElementAtOrDefault(1) ?? "(none)");
        Check("Ctrl+A and Enter become one key step", text.Count > 2 && text[2] == "Teclas: Ctrl+A Enter", text.ElementAtOrDefault(2) ?? "(none)");
        Check("the stop key itself is not recorded", text.Count == 3, $"{text.Count} steps");
        Check("every step is valid", steps.All(s => Apparatus.Core.Actions.StepCatalog.Validate(s) is null), "");
    }

    // ---------------------------------------------------------- sync folder

    /// <summary>Starts the real app (isolated profile, no key is pressed) with a sync folder and looks at the file it keeps there.</summary>
    private static void SyncChecks(string exe, string root)
    {
        if (Process.GetProcessesByName("Apparatus").Length > 0) { Report.Add("SKIP  sync: an Apparatus instance is already running"); return; }
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string profile = Path.Combine(root, "profile"), folder = Path.Combine(root, "sync");
        Directory.CreateDirectory(Path.Combine(profile, "config"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, Apparatus.Core.Backup.SyncPolicy.FileName);

        void Settings() => File.WriteAllText(Path.Combine(profile, "config", "settings.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            IndexFiles = false, SnippetExpansion = false, ExtensionSampleInstalled = true, SyncFolder = folder, MaxResults = 9,
        }));

        void RunApp(int seconds)
        {
            var psi = new ProcessStartInfo(exe, "--background") { UseShellExecute = false };
            psi.Environment["APPARATUS_PROFILE_DIR"] = profile;
            using var app = Process.Start(psi)!;
            Thread.Sleep(seconds * 1000);
            try { app.Kill(true); app.WaitForExit(3000); } catch (InvalidOperationException) { }
            Thread.Sleep(500);
        }

        // 1) first start with an empty sync folder: a valid backup appears by itself
        Settings();
        RunApp(16);
        bool exists = File.Exists(file);
        Check("the app writes the backup into the sync folder by itself", exists, "no file after 16 s");
        if (exists)
        {
            using var s = File.OpenRead(file);
            var content = Apparatus.Core.Backup.BackupBundle.Read(s);
            Check("it is a valid backup from this computer", content.Manifest.Machine == Environment.MachineName && content.SettingsJson is not null, content.Manifest.Machine);
            Check("it carries the user's settings", content.SettingsJson!.Contains("\"MaxResults\": 9"), "MaxResults missing");
            Check("it does not carry the sync folder path itself", !content.SettingsJson.Contains(folder.Replace("\\", "\\\\")), "path leaked");
        }

        // 2) a newer file from another computer must not be overwritten
        var foreign = new Apparatus.Core.Backup.BackupContent(
            new Apparatus.Core.Backup.BackupManifest { Machine = "OUTRO-PC", CreatedUtc = DateTime.UtcNow.AddMinutes(5), Snippets = 0 },
            "{\"MaxResults\":4}", null, null, null);
        using (var o = File.Create(file)) Apparatus.Core.Backup.BackupBundle.Write(o, foreign);
        byte[] before = File.ReadAllBytes(file);
        RunApp(16);
        Check("a newer file from another computer is left untouched", File.ReadAllBytes(file).AsSpan().SequenceEqual(before), "the file was overwritten");
        string log = File.ReadAllText(Path.Combine(profile, "data", "logs", "apparatus.log"));
        Check("and the app says why", log.Contains("Não gravei") && log.Contains("OUTRO-PC"), "no hold-back message in the log");
        Check("without importing anything by itself", File.ReadAllText(Path.Combine(profile, "config", "settings.json")).Contains("\"MaxResults\":9"), "settings changed");
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
