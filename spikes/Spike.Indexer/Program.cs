// Spike: real-world cost of the compact file index on the given roots (build time, memory, query latency).
using System.Diagnostics;
using Apparatus.Core.Indexing;
using Apparatus.Core.Search;
using Apparatus.Core.Settings;

var roots = args.Length > 0 ? args : new[] { @"D:\" };
var settings = new AppSettings();
var options = FileIndex.BuildOptions(settings);

long before = GC.GetTotalMemory(true);
var segments = new List<IndexSegment>();
foreach (var root in roots)
{
    var sw = Stopwatch.StartNew();
    var seg = IndexSegment.Build(root, options, CancellationToken.None,
        n => { if (n % 500_000 < 5000 && sw.Elapsed.TotalSeconds > 1) Console.Write($"\r  {root} ... {n:N0}   "); });
    Console.WriteLine($"\r{root}: {seg.Count:N0} entries in {sw.Elapsed.TotalSeconds:F1}s{(seg.Truncated ? " (TRUNCATED)" : "")}   ");
    segments.Add(seg);
    var snap = Path.Combine(Path.GetTempPath(), "spike-index.bin");
    var s2 = Stopwatch.StartNew();
    seg.Save(snap);
    Console.WriteLine($"  snapshot {new FileInfo(snap).Length / 1048576.0:F0} MB, saved in {s2.ElapsedMilliseconds} ms");
    s2.Restart();
    var loaded = IndexSegment.TryLoad(snap);
    Console.WriteLine($"  snapshot loaded in {s2.ElapsedMilliseconds} ms ({loaded?.Count:N0} entries)");
    File.Delete(snap);
}
GC.Collect();
long after = GC.GetTotalMemory(true);
int total = segments.Sum(s => s.Count);
Console.WriteLine($"TOTAL {total:N0} entries | index memory ≈ {(after - before) / 1048576.0:F0} MB ({(after - before) / (double)Math.Max(1, total):F0} bytes/entry)");

// Query latency through the same parallel path FileIndex uses.
foreach (var q in new[] { "relatorio", "apparatus", "pro", "photoshop", "a", "zzzq", "disco backup", "mp4" })
{
    var fq = FuzzyQuery.Parse(q);
    var times = new List<double>();
    int hits = 0;
    for (int i = 0; i < 4; i++)
    {
        var sw = Stopwatch.StartNew();
        var found = new List<(int Score, int Segment, int Index)>();
        for (int s = 0; s < segments.Count; s++)
            segments[s].Search(fq, s, 72, l => { lock (found) found.AddRange(l); }, true, true, CancellationToken.None);
        var top = found.OrderByDescending(f => f.Score).Take(24).ToList();
        hits = top.Count;
        times.Add(sw.Elapsed.TotalMilliseconds);
    }
    Console.WriteLine($"query '{q,-14}' top={hits,2} | ms: {string.Join(", ", times.Select(t => t.ToString("F0")))}");
}

