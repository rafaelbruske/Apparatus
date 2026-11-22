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
// Windows-key feature on, and TAPS THE REAL WINDOWS KEY with SendInput. While it runs (about 20 s) do not touch
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
    private static string _lastStep = "(start)";

    /// <summary>Progress marker, written to the file named by E2E_REPORT_FILE as it happens (so a stall shows where it is).</summary>
    private static void Step(string text)
    {
        _lastStep = text;
        Emit($"STEP  {DateTime.Now:HH:mm:ss.fff}  {text}");
    }

    private static void Emit(string line)
    {
        Console.WriteLine(line);
        if (Environment.GetEnvironmentVariable("E2E_REPORT_FILE") is { Length: > 0 } path)
        {
            try { File.AppendAllText(path, line + Environment.NewLine); } catch (IOException) { }
        }
    }

    /// <summary>Real-input runs must never hang a person's session: after this long the run is cut and says where it was.</summary>
    private static void StartWatchdog(int seconds)
    {
        new Thread(() =>
        {
            Thread.Sleep(seconds * 1000);
            Emit($"WATCHDOG  stuck for {seconds}s at: {_lastStep}");
            foreach (var p in Process.GetProcessesByName("Apparatus")) { try { p.Kill(true); } catch { } }
            Environment.Exit(9);
        }) { IsBackground = true }.Start();
    }
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
            StartButtonChecks();
            HookLifecycleChecks();
            if (args.Length >= 3 && args[0] == "--inject-failsafe") { StartWatchdog(150); FailsafeScenarios(args[1], args[2]); }
            if (args.Length >= 3 && args[0] == "--inject-win") WinTapChecks(args[1], args[2]);
            if (args.Length >= 3 && args[0] == "--sync") SyncChecks(args[1], args[2]);
            if (args.Length >= 1 && args[0] == "--record") RecordChecks();
            KeyMenuChecks();
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

    // ------------------------------------------------- Start button + failsafe (safe part)

    private static void StartButtonChecks()
    {
        var rects = StartButtonLocator.FindNow();
        Report.Add("INFO  Start button(s): " + string.Join("; ", rects.Select(r => $"{r.Left},{r.Top}-{r.Right},{r.Bottom}")));
        Check("UI Automation finds the Start button of the taskbar", rects.Count >= 1, "none found");
        if (rects.Count >= 1)
        {
            var r = rects[0];
            var tb = TaskbarInfo.Get();
            Check("it is a small target, on the taskbar", r.Right - r.Left is > 10 and < 400 && r.Bottom - r.Top is > 10 and < 200
                && tb is { } t && r.Top >= t.Top - 2 && r.Bottom <= t.Bottom + 2, $"{r}");
        }
        Check("Windows' own Start menu is closed right now", !OfficialStart.IsOpen(), "reported open");

        string build = OsBuild.Current();
        Report.Add($"INFO  Windows build: {build}");
        Check("the Windows build reads as build.revision", System.Text.RegularExpressions.Regex.IsMatch(build, @"^\d{5}(\.\d+)?$"), build);
    }

    // --------------------------------------- Start button + failsafe (real input)

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(150);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);   // left down
        Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);   // left up
    }

    private sealed class Scenario : IDisposable
    {
        private readonly Process _app;
        public readonly string Profile;

        public Scenario(string exe, string profile, string settingsJson, Dictionary<string, string>? env = null)
        {
            Profile = profile;
            if (Directory.Exists(profile)) Directory.Delete(profile, true);
            Directory.CreateDirectory(Path.Combine(profile, "config"));
            File.WriteAllText(Path.Combine(profile, "config", "settings.json"), settingsJson);
            var psi = new ProcessStartInfo(exe, "--background") { UseShellExecute = false };
            psi.Environment["APPARATUS_PROFILE_DIR"] = profile;
            if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
            _app = Process.Start(psi)!;
            for (int i = 0; i < 100 && !LogText().Contains("Tray icon added"); i++) Thread.Sleep(100);
        }

        public string LogText() => Log(Profile);

        public string SettingsText() => File.ReadAllText(Path.Combine(Profile, "config", "settings.json"));

        public void Dispose()
        {
            try { _app.Kill(true); _app.WaitForExit(3000); } catch (InvalidOperationException) { }
            _app.Dispose();
            Thread.Sleep(400);
        }
    }

    private const string StartOn = "{\"IndexFiles\":false,\"SnippetExpansion\":false,\"ExtensionSampleInstalled\":true,\"StartMenuEnabled\":true,\"StartButtonClick\":true}";

    /// <summary>
    /// Three runs of the real app with the real mouse and keyboard: a click on the Start button, a Windows-key tap while the UI is frozen
    /// (the safety net must open Windows' own menu), and a tap when the masking key is sabotaged (the failsafe must switch the
    /// replacement off). Moves the pointer and clicks on the taskbar; about 50 seconds. Do not touch the mouse or keyboard.
    /// </summary>
    /// <summary>
    /// Closes Windows' Start menu by tapping the Windows key again (it toggles). NEVER with Esc: Esc goes to whatever window is in front,
    /// and when that is the Claude Code window it interrupts the running session, which cut two earlier runs of this test short.
    /// </summary>
    private static void CloseOfficial()
    {
        for (int i = 0; i < 3 && OfficialStart.IsOpen(); i++)
        {
            Tap(0x5B);
            Thread.Sleep(700);
        }
    }

    private static void FailsafeScenarios(string exe, string root)
    {
        if (Process.GetProcessesByName("Apparatus").Length > 0) { Report.Add("SKIP  failsafe: an Apparatus instance is already running"); return; }
        GetCursorPos(out var home);
        try
        {
            // ---- 0) positive control: the detector must see Windows' own menu when it is really open (Windows 11 26300 hosts it in
            //         SearchHost, window "Pesquisar"); "Windows' menu stays closed" checks mean nothing unless this passes
            Step("scenario 0: control, open Windows' Start with Ctrl+Esc");
            OfficialStart.Open();
            bool seen = OfficialStart.OpensWithin(TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(30));
            Check("[control] the detector sees Windows' own Start menu when it is open", seen, "not seen");
            CloseOfficial();
            Check("[control] and sees it closed again", !OfficialStart.IsOpen(), "still reported open");

            Step("scenario 1: click on the Start button");
            using (var s = new Scenario(exe, Path.Combine(root, "click"), StartOn))
            {
                Thread.Sleep(4500);   // the locator finds the button within ~1.5 s of the hook starting
                var rects = StartButtonLocator.FindNow();
                if (rects.Count == 0) { Check("[click] the Start button was found", false, "none"); }
                else
                {
                    var r = rects[0];
                    Click((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
                    Thread.Sleep(1500);
                    Check("[click] a click on the Start button opens Apparatus' menu", s.LogText().Contains("Start menu shown"), "no 'Start menu shown'");
                    Check("[click] and Windows' own menu stays closed", !OfficialStart.IsOpen(), "Windows' menu is open");
                    Click((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);   // a second click closes it
                    Thread.Sleep(900);
                    Check("[click] a second click closes it", Regex(s.LogText(), "Start menu hidden") >= 1, "not hidden");
                    Check("[click] it counted as working (nothing tripped)", !File.Exists(Path.Combine(s.Profile, "data", "start-failsafe.json"))
                        || !File.ReadAllText(Path.Combine(s.Profile, "data", "start-failsafe.json")).Contains("\"TrippedReason\": \"o"), "tripped");
                }
            }

            // ---- 2) a frozen Apparatus must not leave the user without a Start menu
            Step("scenario 2: frozen UI");
            using (var s = new Scenario(exe, Path.Combine(root, "freeze"), StartOn, new() { ["APPARATUS_DEBUG_FREEZE_UI_MS"] = "14000" }))
            {
                Thread.Sleep(4000 + 3500);   // frozen from second 4 on; the hooks notice after 2 s of silence
                int shownBefore = Regex(s.LogText(), "Start menu shown");
                Step("[frozen] tapping the Windows key");
                Tap(0x5B);
                Thread.Sleep(1500);
                Step("[frozen] looking for Windows' menu");
                bool official = OfficialStart.IsOpen();
                Check("[frozen] with the UI frozen, the Windows key opens Windows' own menu", official, "Windows' menu did not open");
                Check("[frozen] and Apparatus' menu is not even attempted", Regex(s.LogText(), "Start menu shown") == shownBefore, "Apparatus tried to show it");
                CloseOfficial();
            }

            // ---- 3) if an update breaks the masking, the failsafe hands the menu back to Windows by itself
            Step("scenario 3: masking sabotaged");
            using (var s = new Scenario(exe, Path.Combine(root, "nomask"), StartOn, new() { ["APPARATUS_DEBUG_NO_MASK"] = "1" }))
            {
                Thread.Sleep(3500);
                Step("[failsafe] tapping the Windows key (masking sabotaged)");
                Tap(0x5B);
                Thread.Sleep(2500);
                Step("[failsafe] reading the outcome");
                bool tripped = s.LogText().Contains("Start failsafe tripped");
                Check("[failsafe] both menus opening at once is noticed and trips the failsafe", tripped, "no trip in the log");
                Check("[failsafe] the replacement is switched off in the settings", s.SettingsText().Contains("\"StartMenuEnabled\": false"), "still enabled");
                Check("[failsafe] the reason is kept", File.Exists(Path.Combine(s.Profile, "data", "start-failsafe.json"))
                    && File.ReadAllText(Path.Combine(s.Profile, "data", "start-failsafe.json")).Contains("TrippedReason"), "no state file");
                Step("[failsafe] closing Windows' menu if it is still open");
                CloseOfficial();

                // and from now on the Windows key is plainly Windows' again
                Thread.Sleep(800);
                Step("[failsafe] tapping the Windows key after the trip");
                Tap(0x5B);
                Thread.Sleep(1500);
                Step("[failsafe] looking for Windows' menu after the trip");
                bool officialNow = OfficialStart.IsOpen();
                Check("[failsafe] afterwards the Windows key opens Windows' menu again", officialNow, "it did not");
                CloseOfficial();
            }
            Step("done");
        }
        finally
        {
            SetCursorPos(home.X, home.Y);
        }
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

            Tap(0x5B);   // the Windows key again closes it (never Esc: see CloseOfficial)
            Thread.Sleep(600);
            Check("a second tap closes the menu", Log(profile).Contains("Start menu hidden"), "no 'Start menu hidden' in the log");

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

    // ------------------------------------------- Alt+Space and the system menu

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public int L, T, R, B;
    }

    /// <summary>
    /// What Windows does after Alt+Space is a request in the window procedure (WM_SYSCOMMAND / SC_KEYMENU, or WM_SYSCHAR for the space);
    /// that request is posted straight to a form of this process, with no key injected, once without and once with the suppressor.
    /// The first case is the control: without the suppressor the system menu must really open, or the second case proves nothing.
    /// </summary>
    private static void KeyMenuChecks()
    {
        const uint WM_SYSCHAR = 0x0106, WM_SYSCOMMAND = 0x0112, WM_CANCELMODE = 0x001F;
        const int GUI_INMENUMODE = 0x4, GUI_SYSTEMMENUMODE = 0x8;

        bool InMenu(System.Windows.Forms.Form f)
        {
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            return GetGUIThreadInfo(GetWindowThreadProcessId(f.Handle, out _), ref info) && (info.flags & (GUI_INMENUMODE | GUI_SYSTEMMENUMODE)) != 0;
        }

        bool Attempt(uint message, IntPtr wParam, bool suppress)
        {
            using var form = new System.Windows.Forms.Form { Text = "key menu target", Width = 300, Height = 160, ShowInTaskbar = false };
            KeyMenuSuppressor? guard = null;
            bool inMenu = false;
            form.Shown += async (_, _) =>
            {
                if (suppress) guard = new KeyMenuSuppressor(form.Handle, () => true);
                PostMessage(form.Handle, message, wParam, message == WM_SYSCOMMAND ? (IntPtr)' ' : IntPtr.Zero);
                await Task.Delay(500);
                inMenu = InMenu(form);
                PostMessage(form.Handle, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                await Task.Delay(100);
                form.Close();
            };
            System.Windows.Forms.Application.Run(form);
            guard?.Dispose();
            return inMenu;
        }

        const int SC_KEYMENU = 0xF100;
        bool plain = Attempt(WM_SYSCOMMAND, (IntPtr)SC_KEYMENU, suppress: false);
        Check("[system menu] control: without the suppressor the request opens the window's system menu", plain, "the menu did not open, so the next check proves nothing");
        if (!plain) return;
        Check("[system menu] with the suppressor the SC_KEYMENU request is dropped", !Attempt(WM_SYSCOMMAND, (IntPtr)SC_KEYMENU, suppress: true), "the menu opened anyway");
        Check("[system menu] and so is the space that follows Alt (WM_SYSCHAR)", !Attempt(WM_SYSCHAR, (IntPtr)' ', suppress: true), "the menu opened anyway");
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
