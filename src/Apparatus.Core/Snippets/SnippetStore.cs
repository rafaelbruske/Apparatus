using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Apparatus.Core.Snippets;

/// <summary>
/// Snippets live in their own database (<c>snippets.db</c>): they are user data worth backing up, unlike the
/// disposable cache/history in <c>apparatus.db</c>. Names, triggers and categories are matched in memory;
/// the content is searched with SQLite FTS5 (accent-insensitive, prefix matching).
/// </summary>
public sealed class SnippetStore : IDisposable
{
    public const int MinTriggerLength = 2;
    public const int MaxTriggerLength = 32;

    private static readonly JsonSerializerOptions PortableJson = new() { WriteIndented = true };

    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private List<Snippet> _cache = new();

    public SnippetStore(string databasePath)
    {
        if (databasePath != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _db = new SqliteConnection($"Data Source={databasePath}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Migrate();
        Reload();
    }

    /// <summary>Raised after any change (create, edit, delete, import).</summary>
    public event EventHandler? Changed;

    /// <summary>A snapshot of every snippet, ordered by name.</summary>
    public IReadOnlyList<Snippet> All
    {
        get { lock (_gate) return _cache; }
    }

    public IReadOnlyList<string> Categories =>
        All.Select(s => s.Category).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
           .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();

    public Snippet? Get(long id) => All.FirstOrDefault(s => s.Id == id);

    // ---------------------------------------------------------------- schema

    private void Migrate()
    {
        long version = Scalar("PRAGMA user_version;");
        if (version >= 1) return;
        Exec("""
            CREATE TABLE snippets(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                trigger_text TEXT NOT NULL DEFAULT '',
                content TEXT NOT NULL,
                category TEXT NOT NULL DEFAULT '',
                tags TEXT NOT NULL DEFAULT '',
                favorite INTEGER NOT NULL DEFAULT 0,
                auto_expand INTEGER NOT NULL DEFAULT 1,
                mode INTEGER NOT NULL DEFAULT 0,
                use_count INTEGER NOT NULL DEFAULT 0,
                last_used INTEGER,
                created INTEGER NOT NULL,
                updated INTEGER NOT NULL);
            CREATE UNIQUE INDEX ux_snippets_trigger ON snippets(trigger_text) WHERE trigger_text <> '';
            CREATE VIRTUAL TABLE snippets_fts USING fts5(
                name, content, category, tags,
                content='snippets', content_rowid='id', tokenize='unicode61 remove_diacritics 2');
            CREATE TRIGGER snippets_ai AFTER INSERT ON snippets BEGIN
                INSERT INTO snippets_fts(rowid, name, content, category, tags) VALUES (new.id, new.name, new.content, new.category, new.tags);
            END;
            CREATE TRIGGER snippets_ad AFTER DELETE ON snippets BEGIN
                INSERT INTO snippets_fts(snippets_fts, rowid, name, content, category, tags) VALUES ('delete', old.id, old.name, old.content, old.category, old.tags);
            END;
            CREATE TRIGGER snippets_au AFTER UPDATE ON snippets BEGIN
                INSERT INTO snippets_fts(snippets_fts, rowid, name, content, category, tags) VALUES ('delete', old.id, old.name, old.content, old.category, old.tags);
                INSERT INTO snippets_fts(rowid, name, content, category, tags) VALUES (new.id, new.name, new.content, new.category, new.tags);
            END;
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            PRAGMA user_version = 1;
            """);
    }

    // ------------------------------------------------------------ validation

    /// <summary>Null when the trigger is acceptable, otherwise a message for the user.</summary>
    /// <remarks>
    /// Triggers expand the moment their last character is typed, so one trigger must never be a prefix of
    /// another (typing ";em" would fire before ";email" can be completed).
    /// </remarks>
    public string? ValidateTrigger(string trigger, long? ignoreId = null)
    {
        if (trigger.Length == 0) return null;
        if (trigger.Length < MinTriggerLength) return $"O gatilho precisa ter ao menos {MinTriggerLength} caracteres.";
        if (trigger.Length > MaxTriggerLength) return $"O gatilho pode ter no máximo {MaxTriggerLength} caracteres.";
        if (trigger.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return "O gatilho não pode conter espaços ou caracteres de controle.";

        foreach (var other in All)
        {
            if (other.Trigger.Length == 0 || other.Id == ignoreId) continue;
            if (other.Trigger == trigger) return $"O gatilho já é usado por “{other.Name}”.";
            if (trigger.StartsWith(other.Trigger, StringComparison.Ordinal))
                return $"“{other.Trigger}” (de “{other.Name}”) é o começo deste gatilho e expandiria antes de ele ser digitado por inteiro.";
            if (other.Trigger.StartsWith(trigger, StringComparison.Ordinal))
                return $"Este gatilho é o começo de “{other.Trigger}” (de “{other.Name}”) e o impediria de expandir.";
        }
        return null;
    }

    // ----------------------------------------------------------------- write

    /// <summary>Creates (Id == 0) or updates a snippet and returns it as stored.</summary>
    public Snippet Save(Snippet snippet)
    {
        string name = snippet.Name.Trim();
        if (name.Length == 0) throw new SnippetValidationException("Dê um nome ao snippet.");
        string trigger = snippet.Trigger.Trim();
        if (ValidateTrigger(trigger, snippet.Id == 0 ? null : snippet.Id) is { } problem) throw new SnippetValidationException(problem);

        long now = Now();
        long id;
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            if (snippet.Id == 0)
            {
                cmd.CommandText = """
                    INSERT INTO snippets(name, trigger_text, content, category, tags, favorite, auto_expand, mode, use_count, last_used, created, updated)
                    VALUES($name, $trigger, $content, $category, $tags, $fav, $auto, $mode, $uses, $last, $now, $now);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$uses", snippet.UseCount);
                cmd.Parameters.AddWithValue("$last", snippet.LastUsedUtc is { } l ? ToUnix(l) : DBNull.Value);
            }
            else
            {
                cmd.CommandText = """
                    UPDATE snippets SET name=$name, trigger_text=$trigger, content=$content, category=$category, tags=$tags,
                        favorite=$fav, auto_expand=$auto, mode=$mode, updated=$now WHERE id=$id;
                    SELECT $id;
                    """;
                cmd.Parameters.AddWithValue("$id", snippet.Id);
            }
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$trigger", trigger);
            cmd.Parameters.AddWithValue("$content", snippet.Content.Replace("\r\n", "\n").Replace('\r', '\n'));
            cmd.Parameters.AddWithValue("$category", snippet.Category.Trim());
            cmd.Parameters.AddWithValue("$tags", string.Join(",", NormalizeTags(snippet.Tags)));
            cmd.Parameters.AddWithValue("$fav", snippet.Favorite ? 1 : 0);
            cmd.Parameters.AddWithValue("$auto", snippet.AutoExpand ? 1 : 0);
            cmd.Parameters.AddWithValue("$mode", (int)snippet.Mode);
            cmd.Parameters.AddWithValue("$now", now);
            id = Convert.ToInt64(cmd.ExecuteScalar());
        }
        Reload();
        Changed?.Invoke(this, EventArgs.Empty);
        return Get(id)!;
    }

    public void Delete(long id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM snippets WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        Reload();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RecordUse(long id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE snippets SET use_count = use_count + 1, last_used = $now WHERE id=$id";
            cmd.Parameters.AddWithValue("$now", Now());
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        // No Changed event: usage stats must not make the manager reload while the user is editing.
        Reload();
    }

    // ---------------------------------------------------------------- search

    /// <summary>Snippets whose name, content, category or tags contain every word of <paramref name="query"/> (prefix match).</summary>
    public IReadOnlyList<Snippet> SearchContent(string query, int limit = 20)
    {
        string? match = BuildMatchExpression(query);
        if (match is null) return Array.Empty<Snippet>();

        var ids = new List<long>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT rowid FROM snippets_fts WHERE snippets_fts MATCH $q ORDER BY rank LIMIT $limit";
            cmd.Parameters.AddWithValue("$q", match);
            cmd.Parameters.AddWithValue("$limit", limit);
            try
            {
                using var r = cmd.ExecuteReader();
                while (r.Read()) ids.Add(r.GetInt64(0));
            }
            catch (SqliteException ex)
            {
                AppLog.Warn($"Snippet FTS query failed for '{query}': {ex.Message}");
                return Array.Empty<Snippet>();
            }
        }
        var byId = All.ToDictionary(s => s.Id);
        return ids.Where(byId.ContainsKey).Select(i => byId[i]).ToList();
    }

    /// <summary>"olá mundo" becomes <c>"olá"* "mundo"*</c>; FTS operators typed by the user are neutralised.</summary>
    internal static string? BuildMatchExpression(string query)
    {
        var tokens = query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(c => char.IsLetterOrDigit(c)).ToArray()))
            .Where(t => t.Length > 0)
            .Take(6)
            .ToList();
        return tokens.Count == 0 ? null : string.Join(' ', tokens.Select(t => $"\"{t}\"*"));
    }

    // -------------------------------------------------------- import/export

    public string ExportJson()
    {
        var items = All.Select(s => new PortableSnippet(s.Name, s.Trigger, s.Content, s.Category, s.Tags.ToList(),
            s.Favorite, s.AutoExpand, s.Mode.ToString())).ToList();
        return JsonSerializer.Serialize(new PortableFile(1, items), PortableJson);
    }

    public sealed record ImportResult(int Imported, int Updated, IReadOnlyList<string> Skipped);

    /// <summary>Imports snippets; entries with the same name are updated, trigger conflicts are skipped and reported.</summary>
    public ImportResult ImportJson(string json)
    {
        PortableFile? file;
        try { file = JsonSerializer.Deserialize<PortableFile>(json, PortableJson); }
        catch (JsonException ex) { throw new SnippetValidationException($"Arquivo inválido: {ex.Message}"); }
        if (file?.Snippets is null) throw new SnippetValidationException("O arquivo não contém snippets.");

        int imported = 0, updated = 0;
        var skipped = new List<string>();
        foreach (var p in file.Snippets)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) { skipped.Add("(sem nome)"); continue; }
            var existing = All.FirstOrDefault(s => s.Name.Equals(p.Name.Trim(), StringComparison.CurrentCultureIgnoreCase));
            var s = existing?.Clone() ?? new Snippet();
            s.Name = p.Name;
            s.Trigger = p.Trigger ?? "";
            s.Content = p.Content ?? "";
            s.Category = p.Category ?? "";
            s.Tags = p.Tags ?? new();
            s.Favorite = p.Favorite;
            s.AutoExpand = p.AutoExpand;
            s.Mode = Enum.TryParse<InsertMode>(p.Mode, true, out var m) ? m : InsertMode.Auto;
            try
            {
                Save(s);
                if (existing is null) imported++; else updated++;
            }
            catch (SnippetValidationException ex)
            {
                skipped.Add($"{p.Name}: {ex.Message}");
            }
        }
        return new ImportResult(imported, updated, skipped);
    }

    private sealed record PortableFile(int Version, List<PortableSnippet> Snippets);

    private sealed record PortableSnippet(string Name, string? Trigger, string? Content, string? Category,
        List<string>? Tags, bool Favorite, bool AutoExpand, string? Mode);

    // ----------------------------------------------------------------- seed

    /// <summary>On the very first run only, adds a few examples that show the variables. Never re-adds deleted ones.</summary>
    public void SeedExamplesOnce()
    {
        if (MetaGet("seeded") is not null) return;
        MetaSet("seeded", "1");
        if (All.Count > 0) return;

        void Add(string name, string trigger, string content, string category) =>
            Save(new Snippet { Name = name, Trigger = trigger, Content = content, Category = category });

        Add("Data de hoje", ";data", "{date}", "Exemplos");
        Add("Data e hora", ";dh", "{date} {time}", "Exemplos");
        Add("Resposta com campos", ";ola", "Olá {field:Nome},\n\n{cursor}\n\nAtenciosamente,\n{user}", "Exemplos");
        Add("Prazo em 7 dias", ";prazo", "Prazo: {date+7d:dddd, dd 'de' MMMM 'de' yyyy}", "Exemplos");
    }

    // -------------------------------------------------------------- helpers

    private static IEnumerable<string> NormalizeTags(IEnumerable<string> tags) =>
        tags.Select(t => t.Trim().Replace(",", "")).Where(t => t.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase);

    private void Reload()
    {
        var list = new List<Snippet>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT id, name, trigger_text, content, category, tags, favorite, auto_expand, mode, use_count, last_used, created, updated
                FROM snippets ORDER BY name COLLATE NOCASE
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new Snippet
                {
                    Id = r.GetInt64(0),
                    Name = r.GetString(1),
                    Trigger = r.GetString(2),
                    Content = r.GetString(3),
                    Category = r.GetString(4),
                    Tags = r.GetString(5).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                    Favorite = r.GetInt32(6) != 0,
                    AutoExpand = r.GetInt32(7) != 0,
                    Mode = (InsertMode)r.GetInt32(8),
                    UseCount = r.GetInt32(9),
                    LastUsedUtc = r.IsDBNull(10) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(10)).UtcDateTime,
                    CreatedUtc = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(11)).UtcDateTime,
                    UpdatedUtc = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(12)).UtcDateTime,
                });
            }
            _cache = list;
        }
    }

    private string? MetaGet(string key)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    private void MetaSet(string key, string value)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value=$v";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

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
