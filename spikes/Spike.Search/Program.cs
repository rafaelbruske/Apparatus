// Spike 4: (a) Windows Search index via OLE DB, (b) installed-app enumeration via shell:AppsFolder.
// Read-only: injects no input and changes nothing on the system.
using System.Data.OleDb;
using System.Diagnostics;

Console.WriteLine("=== Spike 4a: Windows Search index (OLE DB SystemIndex) ===");
var cs = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows'";
try
{
    var sw = Stopwatch.StartNew();
    using var conn = new OleDbConnection(cs);
    conn.Open();
    Console.WriteLine($"connection open: {sw.ElapsedMilliseconds} ms (cold)");

    foreach (var drive in new[] { "C:", "D:", "E:" })
    {
        try
        {
            using var c = new OleDbCommand($"SELECT TOP 3 System.ItemPathDisplay FROM SystemIndex WHERE SCOPE='file:{drive}\'", conn);
            using var rd = c.ExecuteReader();
            int n = 0; while (rd.Read()) n++;
            Console.WriteLine($"  indexed content on {drive}: {(n > 0 ? "yes" : "NO results")}");
        }
        catch (Exception ex) { Console.WriteLine($"  scope {drive}: {ex.Message}"); }
    }

    foreach (var term in new[] { "rela", "apparatus", "foto", "config", "doc", "xyzzy" })
    {
        var times = new List<long>();
        int rows = 0;
        string? sample = null;
        for (int i = 0; i < 5; i++)
        {
            sw.Restart();
            using var cmd = new OleDbCommand(
                "SELECT TOP 20 System.ItemNameDisplay, System.ItemPathDisplay FROM SystemIndex " +
                $"WHERE System.ItemNameDisplay LIKE '{term}%' ORDER BY System.Search.Rank DESC", conn);
            using var r = cmd.ExecuteReader();
            rows = 0;
            while (r.Read()) { rows++; sample ??= r.GetString(1); }
            times.Add(sw.ElapsedMilliseconds);
        }
        Console.WriteLine($"  '{term,-10}' rows={rows,2} | ms per query (5 runs): {string.Join(", ", times)} | e.g. {sample}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Spike 4b: shell:AppsFolder enumeration ===");
try
{
    var sw = Stopwatch.StartNew();
    var shellType = Type.GetTypeFromProgID("Shell.Application")!;
    dynamic shell = Activator.CreateInstance(shellType)!;
    dynamic folder = shell.NameSpace("shell:AppsFolder");
    var apps = new List<(string Name, string Id)>();
    foreach (dynamic item in folder.Items())
        apps.Add(((string)item.Name, (string)item.Path));
    Console.WriteLine($"{apps.Count} apps in {sw.ElapsedMilliseconds} ms (cold)");

    sw.Restart();
    int n = 0;
    foreach (dynamic item in folder.Items()) { _ = (string)item.Name; n++; }
    Console.WriteLine($"second pass: {n} apps in {sw.ElapsedMilliseconds} ms");

    var packaged = apps.Count(a => a.Id.Contains('!'));
    Console.WriteLine($"packaged/UWP (AUMID with '!'): {packaged} | classic Win32: {apps.Count - packaged}");
    foreach (var a in apps.Take(6)) Console.WriteLine($"  {a.Name}  ->  {a.Id}");
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
}
