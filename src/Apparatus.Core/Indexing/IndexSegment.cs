using System.Buffers;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using Apparatus.Core.Search;

namespace Apparatus.Core.Indexing;

public sealed class IndexOptions
{
    /// <summary>Directory names (any depth) that are skipped entirely.</summary>
    public IReadOnlyCollection<string> ExcludedNames { get; init; } = Array.Empty<string>();

    /// <summary>Full directory paths that are skipped entirely.</summary>
    public IReadOnlyCollection<string> ExcludedPaths { get; init; } = Array.Empty<string>();

    public int MaxEntries { get; init; } = 3_000_000;
}

/// <summary>
/// Compact, immutable index of one root folder. Names live in one contiguous char array and entries are
/// parallel arrays (offset, length, parent, flags) instead of an object per file, so millions of entries
/// cost tens of megabytes. Entry 0 is the root itself; its "name" is the full root path.
/// </summary>
public sealed class IndexSegment
{
    private const int Magic = 0x58495041; // 'APIX'
    private const int Version = 1;
    private const byte FlagDirectory = 1;

    internal readonly char[] Names;
    internal readonly int[] Offsets;
    internal readonly ushort[] Lengths;
    internal readonly int[] Parents;
    internal readonly byte[] Flags;

    public string Root { get; }
    public int Count { get; }
    public DateTime ScannedAtUtc { get; }
    public bool Truncated { get; }

    internal IndexSegment(string root, char[] names, int[] offsets, ushort[] lengths, int[] parents, byte[] flags,
        int count, DateTime scannedAtUtc, bool truncated)
    {
        Root = root;
        Names = names;
        Offsets = offsets;
        Lengths = lengths;
        Parents = parents;
        Flags = flags;
        Count = count;
        ScannedAtUtc = scannedAtUtc;
        Truncated = truncated;
    }

    public bool IsDirectory(int index) => (Flags[index] & FlagDirectory) != 0;

    public ReadOnlySpan<char> NameSpan(int index) => Names.AsSpan(Offsets[index], Lengths[index]);

    public string GetName(int index)
    {
        var name = NameSpan(index).ToString();
        return Parents[index] < 0 ? Path.GetFileName(name.TrimEnd('\\')) is { Length: > 0 } n ? n : name : name;
    }

    public string GetPath(int index)
    {
        if (Parents[index] < 0) return NameSpan(index).ToString();
        Span<int> chain = stackalloc int[128];
        int depth = 0;
        for (int i = index; i >= 0 && depth < chain.Length; i = Parents[i]) chain[depth++] = i;
        var sb = new System.Text.StringBuilder(260);
        for (int d = depth - 1; d >= 0; d--)
        {
            var part = NameSpan(chain[d]);
            if (sb.Length > 0 && sb[^1] != '\\') sb.Append('\\');
            sb.Append(part);
        }
        return sb.ToString();
    }

    public string GetParentPath(int index) => Parents[index] < 0 ? string.Empty : GetPath(Parents[index]);

    // ---------------------------------------------------------------- build

    public static IndexSegment Build(string root, IndexOptions options, CancellationToken ct, Action<int>? progress = null)
    {
        var b = new Builder();
        var rootPath = root.EndsWith('\\') ? root : root + "\\";
        int rootIndex = b.Add(rootPath, -1, isDirectory: true);

        var excludedNames = new HashSet<string>(options.ExcludedNames, StringComparer.OrdinalIgnoreCase);
        var nameLookup = excludedNames.GetAlternateLookup<ReadOnlySpan<char>>();
        var excludedPaths = new HashSet<string>(
            options.ExcludedPaths.Select(p => p.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);

        var enumOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.System,
            MatchType = MatchType.Win32,
        };

        bool truncated = false;
        var stack = new Stack<(int Index, string Path)>();
        stack.Push((rootIndex, rootPath));
        var pending = new List<(int Index, string Path)>();

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (parentIndex, parentPath) = stack.Pop();
            if (b.Count >= options.MaxEntries) { truncated = true; break; }

            pending.Clear();
            try
            {
                var entries = new FileSystemEnumerable<int>(parentPath, (ref FileSystemEntry e) =>
                {
                    bool isDir = e.IsDirectory;
                    if (isDir && nameLookup.Contains(e.FileName)) return -1;
                    int idx = b.Add(e.FileName, parentIndex, isDir);
                    if (isDir)
                    {
                        var full = e.ToFullPath();
                        if (!excludedPaths.Contains(full) && !IsLink(e, full))
                        {
                            pending.Add((idx, full));
                        }
                    }
                    return idx;
                }, enumOptions);

                foreach (var _ in entries)
                {
                    if (b.Count >= options.MaxEntries) { truncated = true; break; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Directory vanished or became inaccessible mid-scan: keep what we have.
            }

            for (int i = pending.Count - 1; i >= 0; i--) stack.Push(pending[i]);
            progress?.Invoke(b.Count);
        }

        return b.ToSegment(root, truncated);
    }

    /// <summary>Symlinks and junctions can loop; cloud placeholders (OneDrive) are reparse points too but are safe.</summary>
    private static bool IsLink(FileSystemEntry e, string fullPath)
    {
        if ((e.Attributes & FileAttributes.ReparsePoint) == 0) return false;
        try { return new DirectoryInfo(fullPath).LinkTarget is not null; }
        catch { return true; }
    }

    private sealed class Builder
    {
        private char[] _names = new char[1 << 20];
        private int[] _offsets = new int[1 << 16];
        private ushort[] _lengths = new ushort[1 << 16];
        private int[] _parents = new int[1 << 16];
        private byte[] _flags = new byte[1 << 16];
        private int _namesLength;

        public int Count { get; private set; }

        public int Add(ReadOnlySpan<char> name, int parent, bool isDirectory)
        {
            if (Count == _offsets.Length)
            {
                int n = Count * 2;
                Array.Resize(ref _offsets, n);
                Array.Resize(ref _lengths, n);
                Array.Resize(ref _parents, n);
                Array.Resize(ref _flags, n);
            }
            if (_namesLength + name.Length > _names.Length)
            {
                Array.Resize(ref _names, Math.Max(_names.Length * 2, _namesLength + name.Length));
            }
            int len = Math.Min(name.Length, ushort.MaxValue);
            name[..len].CopyTo(_names.AsSpan(_namesLength));
            _offsets[Count] = _namesLength;
            _lengths[Count] = (ushort)len;
            _parents[Count] = parent;
            _flags[Count] = isDirectory ? FlagDirectory : (byte)0;
            _namesLength += len;
            return Count++;
        }

        public IndexSegment ToSegment(string root, bool truncated)
        {
            // Trim to size so the long-lived arrays do not carry doubling slack.
            return new IndexSegment(root,
                _names.AsSpan(0, _namesLength).ToArray(),
                _offsets.AsSpan(0, Count).ToArray(),
                _lengths.AsSpan(0, Count).ToArray(),
                _parents.AsSpan(0, Count).ToArray(),
                _flags.AsSpan(0, Count).ToArray(),
                Count, DateTime.UtcNow, truncated);
        }
    }

    // --------------------------------------------------------------- search

    /// <summary>Scores every entry in parallel and pushes the best into <paramref name="sink"/>.</summary>
    public void Search(FuzzyQuery query, int segmentId, int perWorkerLimit, Action<List<(int Score, int Segment, int Index)>> sink,
        bool includeFiles, bool includeDirectories, CancellationToken ct)
    {
        const int Chunk = 1 << 16;
        int chunks = (Count + Chunk - 1) / Chunk;
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            () => new TopList(perWorkerLimit),
            (c, _, local) =>
            {
                int from = c * Chunk, to = Math.Min(Count, from + Chunk);
                for (int i = Math.Max(from, 1); i < to; i++)
                {
                    bool isDir = (Flags[i] & FlagDirectory) != 0;
                    if (isDir ? !includeDirectories : !includeFiles) continue;
                    int s = FuzzyMatcher.Score(Names.AsSpan(Offsets[i], Lengths[i]), query);
                    if (s != FuzzyMatcher.NoMatch) local.Add(s, segmentId, i);
                }
                return local;
            },
            local => sink(local.ToList()));
    }

    private sealed class TopList
    {
        private readonly PriorityQueue<(int Segment, int Index), int> _heap = new();
        private readonly int _limit;

        public TopList(int limit) => _limit = limit;

        public void Add(int score, int segment, int index)
        {
            if (_heap.Count < _limit) { _heap.Enqueue((segment, index), score); return; }
            if (_heap.TryPeek(out _, out int min) && score > min)
            {
                _heap.Dequeue();
                _heap.Enqueue((segment, index), score);
            }
        }

        public List<(int Score, int Segment, int Index)> ToList()
        {
            var list = new List<(int, int, int)>(_heap.Count);
            while (_heap.TryDequeue(out var item, out int score)) list.Add((score, item.Segment, item.Index));
            return list;
        }
    }

    // ------------------------------------------------------------- snapshot

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(Root);
            w.Write(ScannedAtUtc.Ticks);
            w.Write(Truncated);
            w.Write(Count);
            w.Write(Names.Length);
            w.Flush();
            fs.Write(MemoryMarshal.AsBytes(Names.AsSpan()));
            fs.Write(MemoryMarshal.AsBytes(Offsets.AsSpan()));
            fs.Write(MemoryMarshal.AsBytes(Lengths.AsSpan()));
            fs.Write(MemoryMarshal.AsBytes(Parents.AsSpan()));
            fs.Write(Flags);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static IndexSegment? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var r = new BinaryReader(fs);
            if (r.ReadInt32() != Magic || r.ReadInt32() != Version) return null;
            string root = r.ReadString();
            var scanned = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            bool truncated = r.ReadBoolean();
            int count = r.ReadInt32();
            int namesLength = r.ReadInt32();

            var names = new char[namesLength];
            var offsets = new int[count];
            var lengths = new ushort[count];
            var parents = new int[count];
            var flags = new byte[count];
            fs.ReadExactly(MemoryMarshal.AsBytes(names.AsSpan()));
            fs.ReadExactly(MemoryMarshal.AsBytes(offsets.AsSpan()));
            fs.ReadExactly(MemoryMarshal.AsBytes(lengths.AsSpan()));
            fs.ReadExactly(MemoryMarshal.AsBytes(parents.AsSpan()));
            fs.ReadExactly(flags);
            return new IndexSegment(root, names, offsets, lengths, parents, flags, count, scanned, truncated);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Ignoring unreadable index snapshot {path}: {ex.Message}");
            return null;
        }
    }
}
