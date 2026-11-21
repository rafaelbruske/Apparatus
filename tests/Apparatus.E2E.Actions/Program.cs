// End-to-end check of actions and hotkeys. Starts Apparatus with an isolated profile that holds a set of test
// actions, then presses real hotkeys while this process's form (and a Notepad window) are in front.
// Keys are only injected while one of those windows is verifiably the foreground window.
//
// Run (takes ~40 s, presses keys on its own and opens Notepad: do not use the keyboard meanwhile):
//   dotnet build src/Apparatus.App -c Debug
//   dotnet build tests/Apparatus.E2E.Actions
//   tests\Apparatus.E2E.Actions\bin\Debug\net10.0-windows\ActionE2eTarget.exe <path to Apparatus.exe> <empty temp folder without spaces>
using System.Diagnostics;
using System.Runtime.InteropServices;
using Apparatus.Core.Actions;

internal static class Native
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public KEYBDINPUT ki; public long pad; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
}

internal sealed class Harness
{
    private const byte Ctrl = 0x11, Alt = 0x12, Shift = 0x10, F1 = 0x70;

    private readonly Form _form = new() { Text = "ActionE2eTarget", Width = 700, Height = 300, TopMost = false, StartPosition = FormStartPosition.CenterScreen };
    private readonly TextBox _box = new() { Multiline = true, Dock = DockStyle.Fill, Font = new Font("Consolas", 12) };
    private readonly List<string> _report = new();
    private int _passed, _failed;
    private int _keysSeenByForm;
    private string _markers = "";
    private string _profile = "";

    public int Run(string exe, string profile)
    {
        _profile = profile;
        _markers = Path.Combine(profile, "markers");
        PrepareProfile();
        if (Process.GetProcessesByName("Apparatus").Length > 0) { Console.WriteLine("ABORT: an Apparatus instance is already running."); return 2; }

        var psi = new ProcessStartInfo(exe, "--background") { UseShellExecute = false };
        psi.Environment["APPARATUS_PROFILE_DIR"] = profile;
        using var app = Process.Start(psi)!;
        string log = Path.Combine(profile, "data", "logs", "apparatus.log");
        for (int i = 0; i < 100 && !ReadShared(log).Contains("Tray icon added"); i++) Thread.Sleep(100);
        Thread.Sleep(2500);   // prewarm + hotkey registration

        _box.KeyDown += (_, e) => { if (e.KeyCode is >= Keys.F1 and <= Keys.F12 && e.Control && e.Alt && e.Shift) _keysSeenByForm++; };
        _form.Controls.Add(_box);
        _form.Shown += (_, _) => Task.Run(() => RunTests(app));
        Application.Run(_form);

        try { app.Kill(); app.WaitForExit(3000); } catch { }
        Console.WriteLine(string.Join("\n", _report));
        Console.WriteLine($"\npassed {_passed}, failed {_failed}");
        Console.WriteLine("--- Apparatus log (warnings and errors) ---");
        foreach (var line in ReadShared(log).Split('\n').Where(l => l.Contains("ERROR") || l.Contains("WARN"))) Console.WriteLine(line.TrimEnd());
        return _failed == 0 ? 0 : 1;
    }

    // --------------------------------------------------------------- setup

    private void PrepareProfile()
    {
        if (Directory.Exists(_profile)) Directory.Delete(_profile, true);
        Directory.CreateDirectory(Path.Combine(_profile, "config"));
        Directory.CreateDirectory(_markers);
        File.WriteAllText(Path.Combine(_profile, "config", "settings.json"), "{ \"IndexFiles\": false, \"SnippetExpansion\": false }");

        using var store = new ActionStore(Path.Combine(_profile, "config", "actions.db"));
        string Marker(string name) => Path.Combine(_markers, name + ".txt");
        ActionStep Write(string name) => ActionStep.Create(StepKind.RunCommand, ("command", $"echo {name}>\"{Marker(name)}\""));
        ActionStep Append(string text, string file = "macro") => ActionStep.Create(StepKind.RunCommand, ("command", $"echo {text}>>\"{Marker(file)}\""));

        ActionDefinition A(string name, string hotkey, ScopeMode scope, string[] apps, params ActionStep[] steps) =>
            new() { Name = name, Hotkey = hotkey, Scope = scope, ScopeApps = apps.ToList(), Steps = steps.ToList() };

        // 1. plain global hotkey
        store.Save(A("global-a", "Ctrl+Alt+Shift+F1", ScopeMode.Everywhere, Array.Empty<string>(), Write("a")));
        // 2. same keys, two different apps
        store.Save(A("scoped-target", "Ctrl+Alt+Shift+F2", ScopeMode.OnlyIn, new[] { "ActionE2eTarget" }, Write("f2-target")));
        store.Save(A("scoped-notepad", "Ctrl+Alt+Shift+F2", ScopeMode.OnlyIn, new[] { "Notepad" }, Write("f2-notepad")));
        // 3. a global one overridden by a scoped one with the same keys
        store.Save(A("global-d", "Ctrl+Alt+Shift+F3", ScopeMode.Everywhere, Array.Empty<string>(), Write("f3-global")));
        store.Save(A("scoped-e", "Ctrl+Alt+Shift+F3", ScopeMode.OnlyIn, new[] { "ActionE2eTarget" }, Write("f3-target")));
        // 4. only registered inside Notepad: must pass through everywhere else
        store.Save(A("only-notepad", "Ctrl+Alt+Shift+F4", ScopeMode.OnlyIn, new[] { "Notepad" }, Write("f4-notepad")));
        // 5. window management
        store.Save(A("left-half", "Ctrl+Alt+Shift+F5", ScopeMode.Everywhere, Array.Empty<string>(),
            ActionStep.Create(StepKind.Window, ("command", "left-half"))));
        // 6. macro with conditions, a wait and typing
        var skipped = Append("m2-skipped");
        skipped.When = new StepCondition { Kind = ConditionKind.ActiveProcessIsNot, Value = "ActionE2eTarget" };
        var titled = Append("m3-title");
        titled.When = new StepCondition { Kind = ConditionKind.WindowTitleContains, Value = "e2etarget" };
        store.Save(A("macro", "Ctrl+Alt+Shift+F6", ScopeMode.Everywhere, Array.Empty<string>(),
            Append("m1-first"), skipped, titled, ActionStep.Create(StepKind.Wait, ("ms", "200")),
            ActionStep.Create(StepKind.TypeText, ("text", "ABC"))));
        // 7. send keys: select all, then delete
        store.Save(A("send-keys", "Ctrl+Alt+Shift+F7", ScopeMode.Everywhere, Array.Empty<string>(),
            ActionStep.Create(StepKind.SendKeys, ("keys", "Ctrl+A Delete"))));
        // 8. a step that fails stops the macro; the next action still works afterwards
        store.Save(A("failing", "Ctrl+Alt+Shift+F8", ScopeMode.Everywhere, Array.Empty<string>(),
            ActionStep.Create(StepKind.RunCommand, ("command", "exit 3")), Write("after-failure")));
    }

    // --------------------------------------------------------------- tests

    private void RunTests(Process app)
    {
        var notepadsBefore = Process.GetProcessesByName("Notepad").Select(p => p.Id).ToHashSet();
        Process? notepad = null;
        try
        {
            if (!EnsureForeground(_form.Handle)) { Abort("could not bring the test form to the foreground"); return; }

            Check("a global hotkey runs its action", () => Press(F1), () => Exists("a"));

            Check("scope: in the target app only its own action runs", () => Press(F1 + 1),
                () => Exists("f2-target") && !Exists("f2-notepad"));

            ClearMarkers();
            Check("scope: the more specific action wins over a global one with the same keys", () => Press(F1 + 2),
                () => Exists("f3-target") && !Exists("f3-global"));

            int before = _keysSeenByForm;
            Check("scope: a hotkey registered only for another app passes through here", () => Press(F1 + 3),
                () => _keysSeenByForm == before + 1 && !Exists("f4-notepad"));

            // --- window management by hotkey
            _form.Invoke(() => { _form.Bounds = new Rectangle(300, 300, 800, 500); });
            Thread.Sleep(200);
            Check("window management by hotkey (left half)", () => Press(F1 + 4), () =>
            {
                var work = _form.Invoke(() => Screen.FromControl(_form).WorkingArea);
                Native.DwmGetWindowAttribute(_form.Handle, 9, out var r, Marshal.SizeOf<Native.RECT>());
                return Math.Abs(r.L - work.Left) <= 1 && Math.Abs(r.T - work.Top) <= 1 && Math.Abs((r.R - r.L) - work.Width / 2) <= 2 && Math.Abs((r.B - r.T) - work.Height) <= 2;
            });

            // --- macro: conditions, wait, typing
            _form.Invoke(() => _box.Clear());
            ClearMarkers();
            Check("macro: runs in order, skips the false condition, types text", () => Press(F1 + 5), () =>
            {
                string lines = File.Exists(Marker("macro")) ? File.ReadAllText(Marker("macro")) : "";
                return lines.Contains("m1-first") && lines.Contains("m3-title") && !lines.Contains("m2-skipped") && TextIs("ABC");
            });

            Check("send keys: Ctrl+A then Delete clears the text box", () => Press(F1 + 6), () => TextIs(""));

            Check("a failing step stops the macro", () => Press(F1 + 7), () => !Exists("after-failure"));

            // --- the other app: Notepad
            notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
            IntPtr npWindow = WaitForWindow("Notepad", notepadsBefore);
            if (npWindow == IntPtr.Zero) { Record("scope: in Notepad", false, "Notepad did not open"); }
            else if (!EnsureForeground(npWindow)) { Record("scope: in Notepad", false, "could not bring Notepad to the foreground"); }
            else
            {
                Thread.Sleep(600);   // let the foreground-change event re-arm the hotkeys
                ClearMarkers();
                Press(F1 + 1);
                Thread.Sleep(900);
                Record("scope: in Notepad the Notepad action runs, not the target's", Exists("f2-notepad") && !Exists("f2-target"),
                    $"f2-notepad={Exists("f2-notepad")} f2-target={Exists("f2-target")}");

                Press(F1 + 2);
                Thread.Sleep(900);
                Record("scope: outside its app the global action takes over again", Exists("f3-global") && !Exists("f3-target"),
                    $"f3-global={Exists("f3-global")} f3-target={Exists("f3-target")}");

                Press(F1 + 3);
                Thread.Sleep(900);
                Record("scope: the Notepad-only hotkey works in Notepad", Exists("f4-notepad"), "");
            }

            // back in the form the Notepad-only hotkey must be released again
            EnsureForeground(_form.Handle);
            Thread.Sleep(600);
            int seen = _keysSeenByForm;
            Press(F1 + 3);
            Thread.Sleep(500);
            Record("scope: hotkeys are released again after leaving Notepad", _keysSeenByForm == seen + 1, $"form saw {_keysSeenByForm - seen} key press(es)");
        }
        catch (Exception ex)
        {
            Record("harness", false, ex.Message);
        }
        finally
        {
            foreach (var p in Process.GetProcessesByName("Notepad").Where(p => !notepadsBefore.Contains(p.Id))) { try { p.Kill(); } catch { } }
            try { notepad?.Kill(); } catch { }
            _form.Invoke(() => _form.Close());
        }
    }

    private void Check(string name, Action act, Func<bool> ok)
    {
        if (!EnsureForeground(_form.Handle)) { Record(name, false, "form lost the foreground"); return; }
        try { act(); } catch (Exception ex) { Record(name, false, ex.Message); return; }
        bool result = false;
        for (int i = 0; i < 40 && !(result = SafeCheck(ok)); i++) Thread.Sleep(100);   // up to 4 s
        Thread.Sleep(300);                                                              // give wrong extra actions time to show up
        result = SafeCheck(ok);
        Record(name, result, result ? "" : "condition not met");
    }

    private static bool SafeCheck(Func<bool> f) { try { return f(); } catch { return false; } }

    private bool TextIs(string expected) => _form.Invoke(() => _box.Text) == expected;

    private string Marker(string name) => Path.Combine(_markers, name + ".txt");

    private bool Exists(string name) => File.Exists(Marker(name));

    private void ClearMarkers() { foreach (var f in Directory.GetFiles(_markers)) File.Delete(f); }

    private void Record(string name, bool ok, string detail)
    {
        if (ok) _passed++; else _failed++;
        _report.Add($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail.Length == 0 ? "" : "  — " + detail)}");
    }

    private void Abort(string why) { _report.Add("ABORT: " + why); _form.Invoke(() => _form.Close()); }

    // ----------------------------------------------------------- windows

    private static string ProcessOf(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName; } catch { return ""; }
    }

    private static IntPtr WaitForWindow(string processName, HashSet<int> ignore)
    {
        for (int i = 0; i < 80; i++)
        {
            foreach (var p in Process.GetProcessesByName(processName))
            {
                if (!ignore.Contains(p.Id) && p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            }
            Thread.Sleep(100);
        }
        return IntPtr.Zero;
    }

    private bool EnsureForeground(IntPtr target)
    {
        for (int i = 0; i < 25; i++)
        {
            if (Native.GetForegroundWindow() == target) return true;
            uint fgThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
            uint me = Native.GetCurrentThreadId();
            Native.AttachThreadInput(me, fgThread, true);
            Native.SetForegroundWindow(target);
            Native.AttachThreadInput(me, fgThread, false);
            Thread.Sleep(120);
        }
        return Native.GetForegroundWindow() == target;
    }

    // ------------------------------------------------------------- input

    /// <summary>Presses Ctrl+Alt+Shift+key. Only sends keys while the foreground window is ours or Notepad.</summary>
    private static void Press(byte key)
    {
        string fg = ProcessOf(Native.GetForegroundWindow());
        if (fg is not ("ActionE2eTarget" or "Notepad")) throw new Exception($"refusing to press keys while '{fg}' is in front");
        Send(Ctrl, false); Send(Alt, false); Send(Shift, false);
        Send(key, false); Send(key, true);
        Send(Shift, true); Send(Alt, true); Send(Ctrl, true);
    }

    private static void Send(byte vk, bool up)
    {
        var input = new Native.INPUT { type = 1, ki = { vk = vk, scan = (ushort)Native.MapVirtualKey(vk, 0), flags = up ? 2u : 0u } };
        Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
        Thread.Sleep(8);
    }

    private static string ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(fs).ReadToEnd();
        }
        catch { return ""; }
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        return new Harness().Run(args[0], args[1]);
    }
}
