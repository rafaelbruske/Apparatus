using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Apparatus.Core.Search;
using Apparatus.Core.Settings;

namespace Apparatus.Core.Indexing;

public sealed record FileHit(string Path, bool IsDirectory, int Score);

public enum RootState { Pending, Scanning, Ready, Failed }

public sealed record RootStatus(string Root, RootState State, int Entries, DateTime? ScannedAtUtc, bool Truncated, TimeSpan? Duration);

/// <summary>
/// Name index of files and folders, one <see cref="IndexSegment"/> per root. Loaded instantly from a
/// snapshot, refreshed by a background scan, and topped up between scans by file-system watchers
/// (new items) and existence checks at query time (deleted items).
/// </summary>
public sealed class FileIndex : IDisposable
{
    private const int MaxDelta = 50_000;

    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, IndexSegment> _segments = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RootStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _delta = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = new();
    private HashSet<string> _excludedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _scanGate = new();
    private CancellationTokenSource? _scanCts;
    private volatile IndexSegment[] _snapshot = Array.Empty<IndexSegment>();

    public FileIndex(string cacheDir) => _cacheDir = cacheDir;

    public event EventHandler? StatusChanged;

    public IReadOnlyList<RootStatus> Status => _status.Values.OrderBy(s => s.Root, StringComparer.OrdinalIgnoreCase).ToList();

    public int TotalEntries => _snapshot.Sum(s => s.Count);

    // ------------------------------------------------------------ roots

    /// <summary>User-configured roots, or automatic ones: user folders plus every non-system fixed drive.</summary>
    public static IReadOnlyList<string> ResolveRoots(AppSettings settings)
    {
        var candidates = new List<string>();
        if (settings.IndexRoots is { } configured)
        {
            candidates.AddRange(configured);
        }
        else
        {
            string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var f in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                Path.Combine(profile, "Downloads"),
                Environment.GetEnvironmentVariable("OneDrive") ?? "",
                Environment.GetEnvironmentVariable("OneDriveConsumer") ?? "",
            })
            {
                if (!string.IsNullOrWhiteSpace(f)) candidates.Add(f);
            }
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady &&
                        !string.Equals(drive.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(drive.RootDirectory.FullName);
                    }
                }
                catch (IOException) { /* drive went away */ }
            }
        }

        var roots = candidates
            .Select(c => { try { return Path.GetFullPath(c).TrimEnd('\\') + "\\"; } catch { return ""; } })
            .Where(c => c.Length > 0 && Directory.Exists(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Drop roots nested inside another root: they are already covered.
        return roots
            .Where(r => !roots.Any(o => !ReferenceEquals(o, r) && r.Length > o.Length &&
                                        r.StartsWith(o, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public static IndexOptions BuildOptions(AppSettings settings)
    {
        string sys = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        return new IndexOptions
        {
            ExcludedNames = settings.ExcludedNames,
            MaxEntries = settings.MaxEntriesPerRoot,
            ExcludedPaths = new[]
            {
                Path.Combine(sys, "Windows"),
                Path.Combine(sys, "Program Files"),
                Path.Combine(sys, "Program Files (x86)"),
                Path.Combine(sys, "ProgramData"),
            },
        };
    }

    private string SnapshotPath(string root)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..12];
        return Path.Combine(_cacheDir, $"index-{hash}.bin");
    }

    // ------------------------------------------------------- load/rebuild

    /// <summary>Loads previous snapshots so search works immediately after start.</summary>
    public void LoadSnapshots(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            var seg = IndexSegment.TryLoad(SnapshotPath(root));
            if (seg is null || !string.Equals(seg.Root, root, StringComparison.OrdinalIgnoreCase)) continue;
            _segments[root] = seg;
            _status[root] = new RootStatus(root, RootState.Ready, seg.Count, seg.ScannedAtUtc, seg.Truncated, null);
        }
        Publish();
    }

    /// <summary>True when any root has no snapshot or one older than <paramref name="maxAge"/>.</summary>
    public bool NeedsRescan(IEnumerable<string> roots, TimeSpan maxAge) =>
        roots.Any(r => !_segments.TryGetValue(r, out var s) || DateTime.UtcNow - s.ScannedAtUtc > maxAge);

    /// <summary>Rescans the given roots in the background. A newer call cancels the previous scan.</summary>
    public Task RebuildAsync(IReadOnlyList<string> roots, AppSettings settings)
    {
        CancellationTokenSource cts;
        lock (_scanGate)
        {
            _scanCts?.Cancel();
            cts = _scanCts = new CancellationTokenSource();
        }

        _excludedNames = new HashSet<string>(settings.ExcludedNames, StringComparer.OrdinalIgnoreCase);
        var options = BuildOptions(settings);

        // Forget roots that are no longer configured.
        foreach (var gone in _segments.Keys.Where(k => !roots.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _segments.TryRemove(gone, out _);
            _status.TryRemove(gone, out _);
        }
        foreach (var root in roots)
        {
            _status.AddOrUpdate(root,
                _ => new RootStatus(root, RootState.Pending, 0, null, false, null),
                (_, old) => old with { State = RootState.Pending });
        }
        Publish();
        StartWatchers(roots);

        return Task.Run(() =>
        {
            // One drive at a time per root keeps HDDs from thrashing; separate drives run in parallel.
            Parallel.ForEach(roots, new ParallelOptions { CancellationToken = cts.Token, MaxDegreeOfParallelism = 3 }, root =>
            {
                var sw = Stopwatch.StartNew();
                _status[root] = (_status.TryGetValue(root, out var prev) ? prev : new RootStatus(root, RootState.Scanning, 0, null, false, null)) with { State = RootState.Scanning };
                StatusChanged?.Invoke(this, EventArgs.Empty);
                try
                {
                    var seg = IndexSegment.Build(root, options, cts.Token);
                    if (cts.IsCancellationRequested) return;
                    _segments[root] = seg;
                    _status[root] = new RootStatus(root, RootState.Ready, seg.Count, seg.ScannedAtUtc, seg.Truncated, sw.Elapsed);
                    Publish();
                    seg.Save(SnapshotPath(root));
                    AppLog.Info($"Indexed {root}: {seg.Count:N0} entries in {sw.Elapsed.TotalSeconds:F1}s{(seg.Truncated ? " (truncated)" : "")}");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AppLog.Error($"Indexing {root} failed", ex);
                    _status[root] = new RootStatus(root, RootState.Failed, 0, null, false, sw.Elapsed);
                    StatusChanged?.Invoke(this, EventArgs.Empty);
                }
            });
            // A finished scan supersedes anything the watchers collected before it.
            if (!cts.IsCancellationRequested) _delta.Clear();
        }, cts.Token).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>Starts file-system watchers without scanning (used when the snapshots are still fresh).</summary>
    public void WatchOnly(IEnumerable<string> roots, AppSettings settings)
    {
        _excludedNames = new HashSet<string>(settings.ExcludedNames, StringComparer.OrdinalIgnoreCase);
        StartWatchers(roots);
    }

    private void Publish()
    {
        _snapshot = _segments.Values.ToArray();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    // ----------------------------------------------------------- watching

    private void StartWatchers(IEnumerable<string> roots)
    {
        StopWatchers();
        foreach (var root in roots)
        {
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                };
                w.Created += (_, e) => AddDelta(root, e.FullPath);
                w.Renamed += (_, e) => { _delta.TryRemove(e.OldFullPath, out bool _); AddDelta(root, e.FullPath); };
                w.Deleted += (_, e) => _delta.TryRemove(e.FullPath, out bool _);
                w.Error += (_, _) => { /* overflow: the next scheduled rescan repairs it */ };
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Cannot watch {root}: {ex.Message}");
            }
        }
    }

    private void StopWatchers()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }

    private void AddDelta(string root, string fullPath)
    {
        if (_delta.Count >= MaxDelta) return;
        // Only the part below the watched root counts: the root itself may live under an excluded name.
        var relative = fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fullPath[root.Length..] : fullPath;
        foreach (var part in relative.Split('\\'))
        {
            if (_excludedNames.Contains(part)) return;
        }
        _delta[fullPath] = Directory.Exists(fullPath);
    }

    // ------------------------------------------------------------- search

    public IReadOnlyList<FileHit> Search(FuzzyQuery query, int limit, CancellationToken ct)
    {
        if (query.IsEmpty) return Array.Empty<FileHit>();
        var segments = _snapshot;
        var candidates = new List<(int Score, int Segment, int Index)>();
        // Keep a generous pool per worker: the path-based re-ranking below can reorder it substantially.
        int perWorker = Math.Max(limit * 8, 200);

        for (int s = 0; s < segments.Length; s++)
        {
            segments[s].Search(query, s, perWorker, list => { lock (candidates) candidates.AddRange(list); },
                includeFiles: true, includeDirectories: true, ct);
        }

        // Re-rank with what the name alone cannot tell: shallow paths and non-build folders are more likely
        // to be what the user wants than the 12th copy of "Resources" under bin\Debug.
        var ranked = new List<FileHit>(candidates.Count + 16);
        foreach (var (score, segId, index) in candidates.OrderByDescending(c => c.Score).Take(Math.Max(limit * 8, 200)))
        {
            var seg = segments[segId];
            string path = seg.GetPath(index);
            ranked.Add(new FileHit(path, seg.IsDirectory(index), score - PathPenalty(path)));
        }

        foreach (var (path, isDir) in _delta)
        {
            int score = FuzzyMatcher.Score(Path.GetFileName(path.AsSpan()), query);
            if (score != FuzzyMatcher.NoMatch) ranked.Add(new FileHit(path, isDir, score - PathPenalty(path)));
        }

        // Verify existence lazily, best first, so deleted items are dropped without stat-ing every candidate.
        var hits = new List<FileHit>(limit);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in ranked.OrderByDescending(h => h.Score))
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(hit.Path)) continue;
            if (hit.IsDirectory ? !Directory.Exists(hit.Path) : !File.Exists(hit.Path)) continue;
            hits.Add(hit);
            if (hits.Count >= limit) break;
        }
        return hits;
    }

    private static readonly string[] NoiseFolders =
    {
        "\\bin\\", "\\obj\\", "\\dist\\", "\\build\\", "\\target\\", "\\vendor\\", "\\venv\\", "\\site-packages\\", "\\.gradle\\",
    };

    /// <summary>Deeper paths and build-output folders rank lower for the same name match.</summary>
    internal static int PathPenalty(string path)
    {
        int depth = 0;
        foreach (char c in path) if (c == '\\') depth++;
        int penalty = Math.Min(depth, 15) * 2;
        foreach (var noise in NoiseFolders)
        {
            if (path.Contains(noise, StringComparison.OrdinalIgnoreCase)) { penalty += 40; break; }
        }
        return penalty;
    }

    public void CancelScan()
    {
        lock (_scanGate) _scanCts?.Cancel();
    }

    public void Dispose()
    {
        CancelScan();
        StopWatchers();
    }
}
