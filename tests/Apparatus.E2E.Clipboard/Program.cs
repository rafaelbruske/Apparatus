// End-to-end check of the clipboard history. Starts Apparatus (isolated profile) three times and copies real text to the
// real Windows clipboard; no keys are pressed. It then reads the history database to see what was recorded.
//
// Run (~25 s; it overwrites your clipboard several times, so do not copy anything meanwhile):
//   dotnet build src/Apparatus.App -c Debug
//   dotnet build tests/Apparatus.E2E.Clipboard
//   tests\Apparatus.E2E.Clipboard\bin\Debug\net10.0-windows\ClipE2eTarget.exe <path to Apparatus.exe> <empty temp folder>
using System.Diagnostics;
using System.Text.Json;
using Apparatus.Core.Clipboard;

internal static class Program
{
    private static readonly List<string> Report = new();
    private static int _passed, _failed;
    private static string _profile = "", _exe = "";

    [STAThread]
    private static int Main(string[] args)
    {
        _exe = args[0];
        _profile = args[1];
        ApplicationConfiguration.Initialize();
        if (Process.GetProcessesByName("Apparatus").Length > 0) { Console.WriteLine("ABORT: an Apparatus instance is already running."); return 2; }

        if (Directory.Exists(_profile)) Directory.Delete(_profile, true);
        Directory.CreateDirectory(Path.Combine(_profile, "config"));
        string me = Process.GetCurrentProcess().ProcessName;
        string? original = Clipboard.ContainsText() ? Clipboard.GetText() : null;   // put the user's text back at the end

        // ---- A: history switched off (the default): nothing may be recorded
        WriteSettings(history: true, excluded: new[] { "KeePass" });   // placeholder, replaced below
        WriteSettings(history: false, excluded: Array.Empty<string>());
        using (Launch())
        {
            Copy("A-should-not-be-recorded");
            Thread.Sleep(900);
        }
        Check("history off: nothing is recorded", Entries().Count == 0, $"{Entries().Count} entries");

        // ---- B: history on
        WriteSettings(history: true, excluded: new[] { "KeePass" });
        using (Launch())
        {
            Copy("B-first copy with acentuação: ação");
            Thread.Sleep(700);
            Copy("B-second copy");
            Thread.Sleep(700);
            Copy("B-first copy with acentuação: ação");   // copying the same text again moves it up, no duplicate
            Thread.Sleep(700);

            var entries = Entries();
            foreach (var e in entries) Report.Add($"INFO  entry #{e.Id} count={e.Count} first={e.FirstUtc:HH:mm:ss} last={e.LastUtc:HH:mm:ss} text='{e.Text}'");
            Check("records copies, newest first, without duplicates",
                entries.Select(e => e.Text).SequenceEqual(new[] { "B-first copy with acentuação: ação", "B-second copy" }),
                string.Join(" | ", entries.Select(e => e.Text)));
            Check("a repeated copy is counted", entries.FirstOrDefault()?.Count == 2, $"count {entries.FirstOrDefault()?.Count}");
            Check("remembers which program the text came from", entries.FirstOrDefault()?.Source == me, $"source '{entries.FirstOrDefault()?.Source}', expected '{me}'");

            CopyMarked("B-private-excluded", "ExcludeClipboardContentFromMonitorProcessing", new byte[] { 1, 0, 0, 0 });
            Thread.Sleep(700);
            CopyMarked("B-private-not-in-history", "CanIncludeInClipboardHistory", new byte[] { 0, 0, 0, 0 });
            Thread.Sleep(700);
            Check("text marked as private by its source is never recorded",
                !Entries().Any(e => e.Text.StartsWith("B-private")), string.Join(" | ", Entries().Select(e => e.Text)));

            CopyMarked("B-allowed-by-flag", "CanIncludeInClipboardHistory", new byte[] { 1, 0, 0, 0 });
            Thread.Sleep(700);
            Check("a source that explicitly allows history is recorded", Entries().Any(e => e.Text == "B-allowed-by-flag"), "");

            Clipboard.SetImage(new Bitmap(8, 8));
            Thread.Sleep(700);
            Check("non-text copies are ignored", Entries().Count == 3, $"{Entries().Count} entries");

            Copy("   \r\n  ");
            Thread.Sleep(500);
            Check("blank text is ignored", Entries().Count == 3, $"{Entries().Count} entries");
        }

        // ---- C: history on, but the program doing the copying is excluded
        WriteSettings(history: true, excluded: new[] { me });
        int before = Entries().Count;
        using (Launch())
        {
            Copy("C-from-an-excluded-program");
            Thread.Sleep(900);
        }
        Check("copies made in an excluded program are not recorded",
            !Entries().Any(e => e.Text.StartsWith("C-")) && Entries().Count == before, string.Join(" | ", Entries().Select(e => e.Text)));

        try { if (original is not null) Clipboard.SetText(original); else Clipboard.Clear(); } catch { }

        Console.WriteLine(string.Join("\n", Report));
        Console.WriteLine($"\npassed {_passed}, failed {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------- helpers

    private static void WriteSettings(bool history, string[] excluded) =>
        File.WriteAllText(Path.Combine(_profile, "config", "settings.json"), JsonSerializer.Serialize(new
        {
            IndexFiles = false,
            SnippetExpansion = false,
            ClipboardHistory = history,
            ClipboardExcludedProcesses = excluded,
            ExtensionSampleInstalled = true,
        }));

    private sealed class Running : IDisposable
    {
        private readonly Process _process;
        public Running(Process p) => _process = p;
        public void Dispose() { try { _process.Kill(true); _process.WaitForExit(3000); } catch { } _process.Dispose(); Thread.Sleep(300); }
    }

    private static Running Launch()
    {
        var psi = new ProcessStartInfo(_exe, "--background") { UseShellExecute = false };
        psi.Environment["APPARATUS_PROFILE_DIR"] = _profile;
        var p = Process.Start(psi)!;
        string log = Path.Combine(_profile, "data", "logs", "apparatus.log");
        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (new StreamReader(fs).ReadToEnd().Contains("Tray icon added")) break;
            }
            catch (IOException) { }
            Thread.Sleep(100);
        }
        Thread.Sleep(1500);
        return new Running(p);
    }

    private static void Copy(string text)
    {
        for (int i = 0; i < 10; i++)
        {
            try { Clipboard.SetDataObject(text, copy: true, retryTimes: 5, retryDelay: 50); return; }
            catch (Exception) { Thread.Sleep(50); }
        }
        throw new Exception("could not set the clipboard");
    }

    private static void CopyMarked(string text, string markerFormat, byte[] markerData)
    {
        var data = new DataObject();
        data.SetText(text);
        data.SetData(markerFormat, new MemoryStream(markerData));
        Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 50);
    }

    private static IReadOnlyList<ClipEntry> Entries()
    {
        string path = Path.Combine(_profile, "data", "clipboard.db");
        if (!File.Exists(path)) return Array.Empty<ClipEntry>();
        using var store = new ClipboardHistoryStore(path);   // a second reader is fine: the database is in WAL mode
        return store.All.ToList();
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (ok) _passed++; else _failed++;
        Report.Add($"{(ok ? "PASS" : "FAIL")}  {name}{(ok ? "" : "  — " + detail)}");
    }
}
