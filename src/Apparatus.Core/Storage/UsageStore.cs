using Apparatus.Core.Search;
using Microsoft.Data.Sqlite;

namespace Apparatus.Core.Storage;

/// <summary>
/// SQLite-backed history. Remembers which results were run (global frecency) and which result was picked
/// for which query ("ch" -> Chrome), and serves the recent list shown for an empty query.
/// Everything is also held in memory, so ranking never touches the disk while typing.
/// </summary>
public sealed class UsageStore : IDisposable
{
    private sealed record Usage(string Key, ResultKind Kind, string Title, string Subtitle, string Payload, string? Icon, int Count, long LastUsed);

    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private readonly Dictionary<string, Usage> _usage = new();
    private readonly Dictionary<(string Query, string Key), (int Count, long Last)> _queryUsage = new();
    private readonly Dictionary<string, FavoriteRow> _favorites = new();
    private long _favoriteSequence;

    private sealed record FavoriteRow(SearchResult Result, long Added);

    public UsageStore(string databasePath)
    {
        if (databasePath != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _db = new SqliteConnection($"Data Source={databasePath}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Migrate();
        LoadAll();
    }

    private void Migrate()
    {
        long version = Scalar("PRAGMA user_version;");
        if (version < 1)
        {
            Exec("""
                CREATE TABLE usage(
                    key TEXT PRIMARY KEY, kind INTEGER NOT NULL, title TEXT NOT NULL, subtitle TEXT NOT NULL,
                    payload TEXT NOT NULL, icon TEXT, count INTEGER NOT NULL, last_used INTEGER NOT NULL);
                CREATE TABLE query_usage(
                    query TEXT NOT NULL, key TEXT NOT NULL, count INTEGER NOT NULL, last_used INTEGER NOT NULL,
                    PRIMARY KEY(query, key));
                CREATE TABLE apps_cache(
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, parsing_name TEXT NOT NULL);
                PRAGMA user_version = 1;
                """);
        }
        if (version < 2)
        {
            Exec("""
                CREATE TABLE favorites(
                    key TEXT PRIMARY KEY, kind INTEGER NOT NULL, title TEXT NOT NULL, subtitle TEXT NOT NULL,
                    payload TEXT NOT NULL, icon TEXT, added INTEGER NOT NULL);
                PRAGMA user_version = 2;
                """);
        }
    }

    private void LoadAll()
    {
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT key, kind, title, subtitle, payload, icon, count, last_used FROM usage";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                _usage[r.GetString(0)] = new Usage(r.GetString(0), (ResultKind)r.GetInt32(1), r.GetString(2),
                    r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6), r.GetInt64(7));
            }
        }
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT query, key, count, last_used FROM query_usage";
            using var r = cmd.ExecuteReader();
            while (r.Read()) _queryUsage[(r.GetString(0), r.GetString(1))] = (r.GetInt32(2), r.GetInt64(3));
        }
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT key, kind, title, subtitle, payload, icon, added FROM favorites ORDER BY added, rowid";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var result = new SearchResult(r.GetString(0), (ResultKind)r.GetInt32(1), r.GetString(2), r.GetString(3),
                    r.GetString(4), 0, r.IsDBNull(5) ? null : r.GetString(5), null, Favorite: true);
                _favorites[result.Key] = new FavoriteRow(result, r.GetInt64(6));
                _favoriteSequence = Math.Max(_favoriteSequence, r.GetInt64(6));
            }
        }
    }

    // -------------------------------------------------------------- icons

    /// <summary>
    /// Replaces the stored icon of remembered results (history and favorites) whose key is in <paramref name="iconsByKey"/>.
    /// Icons are remembered with each result, so one that was wrong (or missing) when the item was first used stays wrong until
    /// the item is used again, unless it is corrected here. A null icon means "show the kind glyph". Returns how many rows changed.
    /// </summary>
    public int UpdateIcons(IReadOnlyDictionary<string, string?> iconsByKey)
    {
        int changed = 0;
        lock (_gate)
        {
            foreach (var (key, icon) in iconsByKey)
            {
                if (_usage.TryGetValue(key, out var u) && u.Icon != icon)
                {
                    _usage[key] = u with { Icon = icon };
                    Update("usage", key, icon);
                    changed++;
                }
                if (_favorites.TryGetValue(key, out var f) && f.Result.Icon != icon)
                {
                    _favorites[key] = f with { Result = f.Result with { Icon = icon } };
                    Update("favorites", key, icon);
                    changed++;
                }
            }
        }
        return changed;
    }

    private void Update(string table, string key, string? icon)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"UPDATE {table} SET icon=$icon WHERE key=$key";   // the table name is one of two literals above
        cmd.Parameters.AddWithValue("$icon", (object?)icon ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------ record

    /// <summary>Call when the user runs a result. <paramref name="query"/> is what they had typed.</summary>
    public void Record(string query, SearchResult result, DateTime? nowUtc = null)
    {
        long now = new DateTimeOffset(nowUtc ?? DateTime.UtcNow).ToUnixTimeSeconds();
        string q = Normalize(query);
        lock (_gate)
        {
            _usage.TryGetValue(result.Key, out var old);
            var u = new Usage(result.Key, result.Kind, result.Title, result.Subtitle, result.Payload, result.Icon,
                (old?.Count ?? 0) + 1, now);
            _usage[result.Key] = u;

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO usage(key, kind, title, subtitle, payload, icon, count, last_used)
                    VALUES($key, $kind, $title, $subtitle, $payload, $icon, $count, $last)
                    ON CONFLICT(key) DO UPDATE SET kind=$kind, title=$title, subtitle=$subtitle, payload=$payload,
                        icon=$icon, count=$count, last_used=$last
                    """;
                cmd.Parameters.AddWithValue("$key", u.Key);
                cmd.Parameters.AddWithValue("$kind", (int)u.Kind);
                cmd.Parameters.AddWithValue("$title", u.Title);
                cmd.Parameters.AddWithValue("$subtitle", u.Subtitle);
                cmd.Parameters.AddWithValue("$payload", u.Payload);
                cmd.Parameters.AddWithValue("$icon", (object?)u.Icon ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$count", u.Count);
                cmd.Parameters.AddWithValue("$last", u.LastUsed);
                cmd.ExecuteNonQuery();
            }

            if (q.Length > 0)
            {
                _queryUsage.TryGetValue((q, result.Key), out var qu);
                _queryUsage[(q, result.Key)] = (qu.Count + 1, now);
                using var cmd = _db.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO query_usage(query, key, count, last_used) VALUES($q, $key, 1, $last)
                    ON CONFLICT(query, key) DO UPDATE SET count = count + 1, last_used = $last
                    """;
                cmd.Parameters.AddWithValue("$q", q);
                cmd.Parameters.AddWithValue("$key", result.Key);
                cmd.Parameters.AddWithValue("$last", now);
                cmd.ExecuteNonQuery();
            }
        }
    }

    // ------------------------------------------------------------- boost

    /// <summary>Extra score for a result, from how often/recently it was run and whether it was picked for this query.</summary>
    public double Boost(string key, string query, DateTime? nowUtc = null)
    {
        long now = new DateTimeOffset(nowUtc ?? DateTime.UtcNow).ToUnixTimeSeconds();
        string q = Normalize(query);
        lock (_gate)
        {
            double boost = 0;
            if (_usage.TryGetValue(key, out var u))
            {
                double days = Math.Max(0, (now - u.LastUsed) / 86400.0);
                boost += 12 * Math.Log(1 + u.Count) * (0.3 + 0.7 * Math.Exp(-days / 45));
            }
            if (q.Length > 0)
            {
                int picked = 0;
                foreach (var ((storedQuery, storedKey), v) in _queryUsage)
                {
                    if (storedKey == key && storedQuery.StartsWith(q, StringComparison.Ordinal)) picked += v.Count;
                }
                if (picked > 0) boost += Math.Min(60, 25 * Math.Log(1 + picked));
            }
            return boost;
        }
    }

    /// <summary>Most relevant history for an empty query, ranked by frecency.</summary>
    public IReadOnlyList<SearchResult> Recent(int limit, DateTime? nowUtc = null)
    {
        lock (_gate)
        {
            return _usage.Values
                .Select(u => (u, score: Boost(u.Key, "", nowUtc)))
                .OrderByDescending(x => x.score)
                .Take(limit)
                .Select(x => new SearchResult(x.u.Key, x.u.Kind, x.u.Title, x.u.Subtitle, x.u.Payload, x.score, x.u.Icon))
                .ToList();
        }
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            _usage.Clear();
            _queryUsage.Clear();
            Exec("DELETE FROM usage; DELETE FROM query_usage;");
        }
    }

    public void Forget(string key)
    {
        lock (_gate)
        {
            _usage.Remove(key);
            foreach (var k in _queryUsage.Keys.Where(k => k.Key == key).ToList()) _queryUsage.Remove(k);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM usage WHERE key=$k; DELETE FROM query_usage WHERE key=$k;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------- favorites

    /// <summary>Results that cannot be pinned: they do not exist outside the moment (a sum, a web-search fallback).</summary>
    public static bool CanFavorite(SearchResult r) => r.Kind is not (ResultKind.Calculator or ResultKind.Web or ResultKind.Clipboard);

    public bool IsFavorite(string key)
    {
        lock (_gate) return _favorites.ContainsKey(key);
    }

    /// <summary>Pinned results in the order they were pinned.</summary>
    public IReadOnlyList<SearchResult> Favorites()
    {
        lock (_gate) return _favorites.Values.OrderBy(f => f.Added).Select(f => f.Result).ToList();
    }

    /// <summary>Pins the result, or unpins it when already pinned. Returns the new state.</summary>
    public bool ToggleFavorite(SearchResult result)
    {
        if (!CanFavorite(result)) return false;
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            if (_favorites.Remove(result.Key))
            {
                cmd.CommandText = "DELETE FROM favorites WHERE key=$k";
                cmd.Parameters.AddWithValue("$k", result.Key);
                cmd.ExecuteNonQuery();
                return false;
            }

            long added = ++_favoriteSequence;
            cmd.CommandText = "INSERT INTO favorites(key, kind, title, subtitle, payload, icon, added) VALUES($k, $kind, $t, $s, $p, $i, $a)";
            cmd.Parameters.AddWithValue("$k", result.Key);
            cmd.Parameters.AddWithValue("$kind", (int)result.Kind);
            cmd.Parameters.AddWithValue("$t", result.Title);
            cmd.Parameters.AddWithValue("$s", result.Subtitle);
            cmd.Parameters.AddWithValue("$p", result.Payload);
            cmd.Parameters.AddWithValue("$i", (object?)result.Icon ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$a", added);
            cmd.ExecuteNonQuery();
            _favorites[result.Key] = new FavoriteRow(result with { Highlight = null, Favorite = true, Score = 0 }, added);
            return true;
        }
    }

    // ------------------------------------------------------- apps cache

    public IReadOnlyList<(string Id, string Name, string ParsingName)> LoadAppsCache()
    {
        lock (_gate)
        {
            var list = new List<(string, string, string)>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, name, parsing_name FROM apps_cache";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
            return list;
        }
    }

    public void SaveAppsCache(IEnumerable<(string Id, string Name, string ParsingName)> apps)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            Exec("DELETE FROM apps_cache;");
            foreach (var (id, name, parsing) in apps)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO apps_cache(id, name, parsing_name) VALUES($id, $n, $p)";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$p", parsing);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ----------------------------------------------------------- helpers

    private static string Normalize(string query) => Text.TextFold.Fold(query.Trim());

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
