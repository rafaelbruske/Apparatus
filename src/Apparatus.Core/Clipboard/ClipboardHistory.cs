using System.Security.Cryptography;
using System.Text;
using Apparatus.Core.Search;
using Apparatus.Core.Search.Providers;
using Apparatus.Core.Settings;
using Microsoft.Data.Sqlite;

namespace Apparatus.Core.Clipboard;

public sealed record ClipEntry(long Id, string Text, string Source, DateTime FirstUtc, DateTime LastUtc, int Count, bool Pinned);

/// <summary>
/// Text the user copied, newest first. The same text is stored once (copying it again moves it to the top),
/// entries expire after a retention period or when there are too many, and pinned entries are never dropped.
/// Lives in its own database because it is the most sensitive data the app keeps.
/// </summary>
public sealed class ClipboardHistoryStore : IDisposable
{
    /// <summary>Longer text is ignored: it is a file's content, not something to find again.</summary>
    public const int MaxTextLength = 64 * 1024;

    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private List<ClipEntry> _cache = new();

    public ClipboardHistoryStore(string databasePath, int maxEntries = 500, int retentionDays = 30)
    {
        MaxEntries = maxEntries;
        RetentionDays = retentionDays;
        if (databasePath != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _db = new SqliteConnection($"Data Source={databasePath}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        if (Scalar("PRAGMA user_version;") < 1)
        {
            Exec("""
                CREATE TABLE clips(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    hash TEXT NOT NULL UNIQUE,
                    text TEXT NOT NULL,
                    source TEXT NOT NULL DEFAULT '',
                    first_used INTEGER NOT NULL,
                    last_used INTEGER NOT NULL,
                    count INTEGER NOT NULL DEFAULT 1,
                    pinned INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX ix_clips_last ON clips(last_used DESC);
                PRAGMA user_version = 1;
                """);
        }
        Reload();
    }

    public int MaxEntries { get; set; }

    public int RetentionDays { get; set; }

    public event EventHandler? Changed;

    /// <summary>Newest first (pinned entries are not moved: they only get a marker).</summary>
    public IReadOnlyList<ClipEntry> All
    {
        get { lock (_gate) return _cache; }
    }

    public ClipEntry? Get(long id) => All.FirstOrDefault(e => e.Id == id);

    // ----------------------------------------------------------------- write

    /// <summary>Records a copy. Returns null for text that is not worth keeping (blank or too long).</summary>
    public ClipEntry? Add(string text, string source, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength) return null;
        long now = ToUnix(nowUtc ?? DateTime.UtcNow);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        long id;
        lock (_gate)
        {
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO clips(hash, text, source, first_used, last_used, count) VALUES($h, $t, $s, $now, $now, 1)
                    ON CONFLICT(hash) DO UPDATE SET last_used = $now, count = count + 1,
                        source = CASE WHEN $s <> '' THEN $s ELSE source END;
                    SELECT id FROM clips WHERE hash = $h;
                    """;
                cmd.Parameters.AddWithValue("$h", hash);
                cmd.Parameters.AddWithValue("$t", text);
                cmd.Parameters.AddWithValue("$s", source);
                cmd.Parameters.AddWithValue("$now", now);
                id = Convert.ToInt64(cmd.ExecuteScalar());
            }
            PruneLocked(now);
        }
        Reload();
        Changed?.Invoke(this, EventArgs.Empty);
        return Get(id);
    }

    public void SetPinned(long id, bool pinned)
    {
        Execute("UPDATE clips SET pinned=$p WHERE id=$id", ("$p", pinned ? 1 : 0), ("$id", id));
    }

    public void Delete(long id) => Execute("DELETE FROM clips WHERE id=$id", ("$id", id));

    /// <summary>Removes everything, except pinned entries when <paramref name="keepPinned"/>.</summary>
    public void Clear(bool keepPinned = true) =>
        Execute(keepPinned ? "DELETE FROM clips WHERE pinned = 0" : "DELETE FROM clips");

    /// <summary>Drops expired and surplus entries. Called after every copy and once a day.</summary>
    public void Prune(DateTime? nowUtc = null)
    {
        lock (_gate) PruneLocked(ToUnix(nowUtc ?? DateTime.UtcNow));
        Reload();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void PruneLocked(long now)
    {
        long cutoff = now - (long)RetentionDays * 86_400_000;
        Exec($"DELETE FROM clips WHERE pinned = 0 AND last_used < {cutoff}");
        Exec($"""
            DELETE FROM clips WHERE pinned = 0 AND id NOT IN
                (SELECT id FROM clips WHERE pinned = 0 ORDER BY last_used DESC, id DESC LIMIT {Math.Max(1, MaxEntries)})
            """);
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
            cmd.ExecuteNonQuery();
        }
        Reload();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- helpers

    private void Reload()
    {
        var list = new List<ClipEntry>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, text, source, first_used, last_used, count, pinned FROM clips ORDER BY last_used DESC, id DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new ClipEntry(r.GetInt64(0), r.GetString(1), r.GetString(2),
                    DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)).UtcDateTime,
                    DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)).UtcDateTime,
                    r.GetInt32(5), r.GetInt32(6) != 0));
            }
            _cache = list;
        }
    }

    /// <summary>Milliseconds: two copies within the same second must still be ordered.</summary>
    private static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Dispose() => _db.Dispose();
}

/// <summary>"clip" in the launcher lists the clipboard history; "clip text" filters it.</summary>
public sealed class ClipboardProvider : ISearchProvider
{
    public const string Keyword = "clip";
    private const double BaseScore = 9000;
    private const int MaxShown = 30;

    private readonly ClipboardHistoryStore _store;
    private readonly Func<AppSettings> _settings;
    private readonly Func<DateTime> _now;

    public ClipboardProvider(ClipboardHistoryStore store, Func<AppSettings> settings, Func<DateTime>? now = null)
    {
        _store = store;
        _settings = settings;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public string Id => "clipboard";

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchContext context, CancellationToken ct)
    {
        string raw = context.Raw;
        bool bare = raw.Equals(Keyword, StringComparison.OrdinalIgnoreCase);
        if (!bare && !(raw.Length > Keyword.Length && raw.StartsWith(Keyword, StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(raw[Keyword.Length])))
            return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());

        if (!_settings().ClipboardHistory)
        {
            return Task.FromResult<IReadOnlyList<SearchResult>>(new[]
            {
                new SearchResult("cmd:" + InternalCommands.ClipboardEnable, ResultKind.Command, "Ativar o histórico da área de transferência",
                    "Está desligado. Enter para guardar o que você copiar (só texto, nunca senhas de gerenciadores)",
                    InternalCommands.ClipboardEnable, BaseScore),
            });
        }

        var query = FuzzyQuery.Parse(bare ? "" : raw[Keyword.Length..]);
        IEnumerable<(ClipEntry Entry, double Score)> ranked;
        if (query.IsEmpty)
        {
            ranked = _store.All.Select((e, i) => (e, BaseScore - i));
        }
        else
        {
            ranked = _store.All
                .Select(e => (Entry: e, Fuzzy: FuzzyMatcher.Score(Preview(e.Text, 300), query)))
                .Where(x => x.Fuzzy != FuzzyMatcher.NoMatch)
                .Select(x => (x.Entry, BaseScore + x.Fuzzy));
        }

        var results = ranked
            .OrderByDescending(x => x.Score)
            .Take(MaxShown)
            .Select(x => ToResult(x.Entry, x.Score, _now()))
            .ToList();
        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    public static SearchResult ToResult(ClipEntry e, double score, DateTime nowUtc)
    {
        string title = Preview(e.Text, 90);
        string source = e.Source.Length > 0 ? $" · {e.Source}" : "";
        string subtitle = $"{Ago(nowUtc - e.LastUtc)}{source} · {e.Text.Length:N0} caracteres" + (e.Count > 1 ? $" · copiado {e.Count}×" : "");
        return new SearchResult($"clip:{e.Id}", ResultKind.Clipboard, title, subtitle, e.Id.ToString(), score, Favorite: e.Pinned);
    }

    /// <summary>First non-blank line, trimmed and cut to <paramref name="max"/> characters.</summary>
    public static string Preview(string text, int max)
    {
        string line = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }

    public static string Ago(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "agora há pouco";
        if (age < TimeSpan.FromHours(1)) return $"há {(int)age.TotalMinutes} min";
        if (age < TimeSpan.FromDays(1)) return $"há {(int)age.TotalHours} h";
        if (age < TimeSpan.FromDays(2)) return "ontem";
        return $"há {(int)age.TotalDays} dias";
    }
}
