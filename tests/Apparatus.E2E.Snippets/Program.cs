// End-to-end check of snippet expansion. Starts Apparatus with an isolated profile, then types into a WinForms
// text box owned by this process. Keys are only injected while this form is verifiably the foreground window.
//
// Run (takes ~35 s and types on its own: do not use the keyboard meanwhile):
//   dotnet build src/Apparatus.App -c Debug
//   dotnet build tests/Apparatus.E2E.Snippets
//   tests\Apparatus.E2E.Snippets\bin\Debug\net10.0-windows\SnippetE2eTarget.exe <path to Apparatus.exe> <empty temp folder>
// Environment: E2E_ONLY=n runs only the first n cases; APPARATUS_DEBUG_KEYS=1 makes Apparatus log every key it holds.
using System.Diagnostics;
using System.Runtime.InteropServices;
using Apparatus.Core.Settings;
using Apparatus.Core.Snippets;

internal static class Native
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern short VkKeyScanEx(char ch, IntPtr hkl);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public KEYBDINPUT ki; public long pad; }   // pad: union is 32 bytes on x64

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
}

internal sealed class Harness
{
    private readonly Form _form = new() { Text = "SnippetE2eTarget", Width = 700, Height = 300, TopMost = true, StartPosition = FormStartPosition.CenterScreen };
    private readonly TextBox _box = new() { Multiline = true, Dock = DockStyle.Fill, Font = new Font("Consolas", 12) };
    private int _passed, _failed, _ran;
    private readonly int _limit = int.TryParse(Environment.GetEnvironmentVariable("E2E_ONLY"), out var n) ? n : int.MaxValue;
    private readonly List<string> _report = new();

    public int Run(string exe, string profile)
    {
        PrepareProfile(profile);
        if (Process.GetProcessesByName("Apparatus").Length > 0) { Console.WriteLine("ABORT: an Apparatus instance is already running."); return 2; }

        var psi = new ProcessStartInfo(exe, "--background") { UseShellExecute = false };
        psi.Environment["APPARATUS_PROFILE_DIR"] = profile;
        using var app = Process.Start(psi)!;
        string log = Path.Combine(profile, "data", "logs", "apparatus.log");
        for (int i = 0; i < 100 && !(File.Exists(log) && ReadShared(log).Contains("Keyboard hook installed")); i++) Thread.Sleep(100);
        if (!ReadShared(log).Contains("Keyboard hook installed")) { Console.WriteLine("ABORT: hook did not install\n" + ReadShared(log)); app.Kill(); return 2; }
        Thread.Sleep(1500);   // let prewarm finish

        _form.Controls.Add(_box);
        _form.Shown += (_, _) => Task.Run(() => RunTests(app, log));
        Application.Run(_form);

        try { app.Kill(); app.WaitForExit(3000); } catch { }
        Console.WriteLine(string.Join("\n", _report));
        Console.WriteLine($"\npassed {_passed}, failed {_failed}");
        Console.WriteLine("--- Apparatus log (errors/warnings) ---");
        foreach (var line in ReadShared(log).Split('\n').Where(l => l.Contains("ERROR") || l.Contains("WARN") || l.Contains("xpand"))) Console.WriteLine(line.TrimEnd());
        return _failed == 0 ? 0 : 1;
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

    private static void PrepareProfile(string profile)
    {
        if (Directory.Exists(profile)) Directory.Delete(profile, true);
        Directory.CreateDirectory(Path.Combine(profile, "config"));
        File.WriteAllText(Path.Combine(profile, "config", "settings.json"), "{ \"IndexFiles\": false }");
        using var store = new SnippetStore(Path.Combine(profile, "config", "snippets.db"));
        store.Save(new Snippet { Name = "t1", Trigger = ";t1", Content = "{date}" });
        store.Save(new Snippet { Name = "t2", Trigger = ";t2", Content = "Olá,\n\n{cursor}\n\nAbraço" });
        store.Save(new Snippet { Name = "t3", Trigger = ";t3", Content = "({cursor})" });
        store.Save(new Snippet { Name = "t4", Trigger = ";t4", Content = "Oi {field:Nome}!" });
        store.Save(new Snippet { Name = "t5", Trigger = ";t5", Content = "{clipboard}!" });
        store.Save(new Snippet { Name = "t6", Trigger = "btw", Content = "by the way" });
        store.Save(new Snippet { Name = "t9", Trigger = ";t9", Content = "NOPE", AutoExpand = false });
    }

    // ------------------------------------------------------------ tests

    private void RunTests(Process app, string log)
    {
        string today = DateTime.Now.ToString("dd/MM/yyyy");
        if (!EnsureForeground()) { Finish("ABORT: could not bring the test form to the foreground"); return; }

        Case("simple expansion, typed at human speed", () => Type(";t1", 35), today);
        Case("expansion inside text, then more typing in a burst", () => Type("hoje: ;t1 ok", 0), $"hoje: {today} ok");
        Case("cursor placement (short text is typed)", () => { Type(";t3", 30); Thread.Sleep(500); Type("X", 30); }, "(X)");

        SetClipboard("COPIED");
        Case("multi-line text is pasted, cursor lands in the middle", () => { Type(";t2", 30); Thread.Sleep(700); Type("X", 30); },
            "Olá,\r\n\r\nX\r\n\r\nAbraço");
        CheckClipboard("clipboard restored after a paste", "COPIED");
        Case("keys typed during an expansion are replayed in order", () => Type(";t2ZZ", 0), "Olá,\r\n\r\nZZ\r\n\r\nAbraço");

        Case("{clipboard} variable", () => Type(";t5", 30), "COPIED!");
        Case("word trigger needs a boundary (no expansion inside a word)", () => Type("abtw", 30), "abtw");
        Case("word trigger after a space", () => Type("abtw btw", 30), "abtw by the way");
        Case("backspace corrects a typo before the trigger completes", () => { Type(";tx", 30); Key(0x08); Type("1", 30); }, today);
        Case("snippet with AutoExpand off stays as typed", () => Type(";t9", 30), ";t9");
        Case("fields dialog: type the value, press Enter", () => FieldsDialog(), "Oi Maria!");

        Finish(null);
    }

    private void Case(string name, Action act, string expected)
    {
        if (_ran++ >= _limit) return;
        Key(0x1B);                       // Esc: forgets any half-typed trigger
        Thread.Sleep(100);
        _form.Invoke(() => { _box.Clear(); _box.Focus(); });
        Thread.Sleep(150);
        if (!EnsureForeground()) { Record(name, false, "form lost the foreground"); return; }
        try { act(); } catch (Exception ex) { Record(name, false, ex.Message); return; }
        Thread.Sleep(800);
        string actual = _form.Invoke(() => _box.Text);
        Record(name, actual == expected, $"expected {Show(expected)}  got {Show(actual)}");
    }

    private void FieldsDialog()
    {
        Type(";t4", 30);
        // The dialog must take the focus before we type into it; never type blindly.
        for (int i = 0; i < 60 && ForegroundProcess() != "Apparatus"; i++) Thread.Sleep(50);
        if (ForegroundProcess() != "Apparatus") throw new Exception("the fields dialog did not appear");
        Thread.Sleep(300);
        Type("Maria", 30);
        Key(0x0D);
        for (int i = 0; i < 80 && ForegroundProcess() == "Apparatus"; i++) Thread.Sleep(50);
        Thread.Sleep(600);
    }

    private void CheckClipboard(string name, string expected)
    {
        string? actual = null;
        _form.Invoke(() => actual = Clipboard.ContainsText() ? Clipboard.GetText() : null);
        Record(name, actual == expected, $"expected {Show(expected)}  got {Show(actual ?? "(none)")}");
    }

    private void SetClipboard(string text) => _form.Invoke(() => Clipboard.SetText(text));

    private void Record(string name, bool ok, string detail)
    {
        if (ok) _passed++; else _failed++;
        _report.Add($"{(ok ? "PASS" : "FAIL")}  {name}{(ok ? "" : "  — " + detail)}");
    }

    private void Finish(string? abort)
    {
        if (abort is not null) _report.Add(abort);
        _form.Invoke(() => _form.Close());
    }

    private static string Show(string s) => "\"" + s.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

    // ---------------------------------------------------------- input

    private bool EnsureForeground()
    {
        for (int i = 0; i < 20; i++)
        {
            if (Native.GetForegroundWindow() == _form.Handle) return true;
            uint fgThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
            uint me = Native.GetCurrentThreadId();
            Native.AttachThreadInput(me, fgThread, true);
            _form.Invoke(() => { _form.Activate(); Native.SetForegroundWindow(_form.Handle); _box.Focus(); });
            Native.AttachThreadInput(me, fgThread, false);
            Thread.Sleep(100);
        }
        return Native.GetForegroundWindow() == _form.Handle;
    }

    private static string ForegroundProcess()
    {
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName; } catch { return ""; }
    }

    private void Type(string text, int delayMs)
    {
        foreach (char c in text)
        {
            // Only inject while our own form is in front: a stray key must never reach another program.
            if (Native.GetForegroundWindow() != _form.Handle && ForegroundProcess() != "Apparatus")
                throw new Exception("lost the foreground while typing");
            short scan = Native.VkKeyScanEx(c, Native.GetKeyboardLayout(0));
            if (scan == -1) throw new Exception($"cannot type '{c}' on this layout");
            byte vk = (byte)(scan & 0xFF);
            bool shift = (scan & 0x100) != 0;
            if (shift) Send(0x10, false);
            Send(vk, false);
            Send(vk, true);
            if (shift) Send(0x10, true);
            if (delayMs > 0) Thread.Sleep(delayMs);
        }
    }

    private void Key(byte vk) { Send(vk, false); Send(vk, true); }

    private static void Send(byte vk, bool up)
    {
        var input = new Native.INPUT { type = 1, ki = { vk = vk, scan = (ushort)Native.MapVirtualKey(vk, 0), flags = up ? 2u : 0u } };
        Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
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
