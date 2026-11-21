using System.Text.Json;
using System.Text.Json.Serialization;
using Apparatus.Core.Input;
using Microsoft.Data.Sqlite;

namespace Apparatus.Core.Actions;

/// <summary>
/// The user's actions and macros, in their own database (<c>actions.db</c>) so they can be backed up with the
/// snippets. Steps are stored as JSON; everything else is plain columns.
/// </summary>
public sealed class ActionStore : IDisposable
{
    private static readonly JsonSerializerOptions StepsJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions PortableJson = new(StepsJson) { WriteIndented = true };

    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private List<ActionDefinition> _cache = new();

    public ActionStore(string databasePath)
    {
        if (databasePath != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _db = new SqliteConnection($"Data Source={databasePath}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Migrate();
        Reload();
    }

    /// <summary>The hotkey of the launcher itself; actions may not take it.</summary>
    public string LauncherHotkey { get; set; } = "";

    /// <summary>Raised after any change except run statistics.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ActionDefinition> All
    {
        get { lock (_gate) return _cache; }
    }

    public IReadOnlyList<string> Categories =>
        All.Select(a => a.Category).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
           .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();

    public ActionDefinition? Get(long id) => All.FirstOrDefault(a => a.Id == id);

    public IReadOnlyList<HotkeyBinding> Bindings => All.Select(HotkeyBinding.From).OfType<HotkeyBinding>().ToList();

    // ---------------------------------------------------------------- schema

    private void Migrate()
    {
        if (Scalar("PRAGMA user_version;") >= 1) return;
        Exec("""
            CREATE TABLE actions(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                category TEXT NOT NULL DEFAULT '',
                hotkey TEXT NOT NULL DEFAULT '',
                scope_mode INTEGER NOT NULL DEFAULT 0,
                scope_apps TEXT NOT NULL DEFAULT '',
                enabled INTEGER NOT NULL DEFAULT 1,
                on_error INTEGER NOT NULL DEFAULT 0,
                steps TEXT NOT NULL,
                run_count INTEGER NOT NULL DEFAULT 0,
                last_run INTEGER,
                created INTEGER NOT NULL,
                updated INTEGER NOT NULL);
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            PRAGMA user_version = 1;
            """);
    }

    // ------------------------------------------------------------ validation

    /// <summary>Null when the action is acceptable, otherwise the first problem, worded for the user.</summary>
    public string? Validate(ActionDefinition action)
    {
        if (action.Name.Trim().Length == 0) return "Dê um nome à ação.";
        if (action.Steps.Count == 0) return "Adicione ao menos um passo.";
        for (int i = 0; i < action.Steps.Count; i++)
        {
            if (StepCatalog.Validate(action.Steps[i]) is { } problem)
                return action.Steps.Count > 1 ? $"Passo {i + 1}: {problem}" : problem;
        }
        if (action.Scope != ScopeMode.Everywhere && action.ScopeApps.All(a => a.Trim().Length == 0))
            return "Informe ao menos um aplicativo para o escopo do atalho.";
        return ValidateHotkey(action);
    }

    /// <summary>Checks only the hotkey part (used live in the editor).</summary>
    public string? ValidateHotkey(ActionDefinition action)
    {
        string text = action.Hotkey.Trim();
        if (text.Length == 0) return null;
        if (!HotkeySpec.TryParse(text, out var spec)) return "Atalho inválido. Use ao menos um modificador (Ctrl, Alt, Shift ou Win) com uma tecla.";
        if (LauncherHotkey.Length > 0 && HotkeySpec.TryParse(LauncherHotkey, out var launcher) && launcher == spec)
            return $"{spec} já é o atalho do launcher.";

        var candidate = new HotkeyBinding(action.Id, spec, action.Scope, action.ScopeApps.Select(HotkeyBinding.NormalizeProcess).Where(a => a.Length > 0).ToList());
        var others = All.Where(a => a.Id != action.Id).Select(HotkeyBinding.From).OfType<HotkeyBinding>();
        if (HotkeyPlan.FindConflict(candidate, others) is { } clash)
            return $"{spec} já é usado por “{Get(clash.ActionId)?.Name}” {(candidate.Scope == ScopeMode.OnlyIn ? "nesses aplicativos" : "no mesmo escopo")}.";
        return null;
    }

    // ----------------------------------------------------------------- write

    public ActionDefinition Save(ActionDefinition action)
    {
        if (Validate(action) is { } problem) throw new ActionValidationException(problem);

        string hotkey = HotkeySpec.TryParse(action.Hotkey, out var spec) ? spec.ToString() : "";
        long now = Now();
        long id;
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            if (action.Id == 0)
            {
                cmd.CommandText = """
                    INSERT INTO actions(name, category, hotkey, scope_mode, scope_apps, enabled, on_error, steps, run_count, last_run, created, updated)
                    VALUES($name, $cat, $hotkey, $scope, $apps, $enabled, $onerr, $steps, $runs, $last, $now, $now);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$runs", action.RunCount);
                cmd.Parameters.AddWithValue("$last", action.LastRunUtc is { } l ? ToUnix(l) : DBNull.Value);
            }
            else
            {
                cmd.CommandText = """
                    UPDATE actions SET name=$name, category=$cat, hotkey=$hotkey, scope_mode=$scope, scope_apps=$apps,
                        enabled=$enabled, on_error=$onerr, steps=$steps, updated=$now WHERE id=$id;
                    SELECT $id;
                    """;
                cmd.Parameters.AddWithValue("$id", action.Id);
            }
            cmd.Parameters.AddWithValue("$name", action.Name.Trim());
            cmd.Parameters.AddWithValue("$cat", action.Category.Trim());
            cmd.Parameters.AddWithValue("$hotkey", hotkey);
            cmd.Parameters.AddWithValue("$scope", (int)action.Scope);
            cmd.Parameters.AddWithValue("$apps", string.Join("|", action.ScopeApps.Select(HotkeyBinding.NormalizeProcess).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)));
            cmd.Parameters.AddWithValue("$enabled", action.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$onerr", (int)action.OnError);
            cmd.Parameters.AddWithValue("$steps", JsonSerializer.Serialize(action.Steps, StepsJson));
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
            cmd.CommandText = "DELETE FROM actions WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        Reload();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetEnabled(long id, bool enabled)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE actions SET enabled=$e, updated=$now WHERE id=$id";
            cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$now", Now());
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        Reload();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RecordRun(long id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE actions SET run_count = run_count + 1, last_run = $now WHERE id=$id";
            cmd.Parameters.AddWithValue("$now", Now());
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        Reload();   // statistics only: no Changed event, so hotkeys are not re-registered after every run
    }

    // -------------------------------------------------------- import/export

    public string ExportJson() => JsonSerializer.Serialize(new PortableFile(1, All.Select(a => new PortableAction(
        a.Name, a.Category, a.Hotkey, a.Scope, a.ScopeApps.ToList(), a.Enabled, a.OnError, a.Steps.Select(s => s.Clone()).ToList())).ToList()), PortableJson);

    public sealed record ImportResult(int Imported, int Updated, IReadOnlyList<string> Skipped);

    /// <summary>Imports actions; same-name ones are updated. Imported hotkeys that clash are dropped (the action is kept).</summary>
    public ImportResult ImportJson(string json)
    {
        PortableFile? file;
        try { file = JsonSerializer.Deserialize<PortableFile>(json, PortableJson); }
        catch (JsonException ex) { throw new ActionValidationException($"Arquivo inválido: {ex.Message}"); }
        if (file?.Actions is null) throw new ActionValidationException("O arquivo não contém ações.");

        int imported = 0, updated = 0;
        var skipped = new List<string>();
        foreach (var p in file.Actions)
        {
            var existing = All.FirstOrDefault(a => a.Name.Equals(p.Name?.Trim(), StringComparison.CurrentCultureIgnoreCase));
            var a = existing?.Clone() ?? new ActionDefinition();
            a.Name = p.Name ?? "";
            a.Category = p.Category ?? "";
            a.Hotkey = p.Hotkey ?? "";
            a.Scope = p.Scope;
            a.ScopeApps = p.ScopeApps ?? new();
            a.Enabled = p.Enabled;
            a.OnError = p.OnError;
            a.Steps = p.Steps ?? new();
            try
            {
                if (ValidateHotkey(a) is not null) a.Hotkey = "";   // keep the action, lose the clashing hotkey
                Save(a);
                if (existing is null) imported++; else updated++;
            }
            catch (ActionValidationException ex)
            {
                skipped.Add($"{p.Name}: {ex.Message}");
            }
        }
        return new ImportResult(imported, updated, skipped);
    }

    private sealed record PortableFile(int Version, List<PortableAction> Actions);

    private sealed record PortableAction(string? Name, string? Category, string? Hotkey, ScopeMode Scope,
        List<string>? ScopeApps, bool Enabled, ErrorPolicy OnError, List<ActionStep>? Steps);

    // ----------------------------------------------------------------- seed

    /// <summary>First run only: a handful of ready-made actions, none of them bound to keys.</summary>
    public void SeedExamplesOnce()
    {
        if (MetaGet("seeded") is not null) return;
        MetaSet("seeded", "1");
        if (All.Count > 0) return;

        void Add(string name, params ActionStep[] steps) =>
            Save(new ActionDefinition { Name = name, Category = "Exemplos", Steps = steps.ToList() });

        ActionStep Window(string cmd) => ActionStep.Create(StepKind.Window, ("command", cmd));
        Add("Janela: metade esquerda", Window("left-half"));
        Add("Janela: metade direita", Window("right-half"));
        Add("Janela: maximizar", Window("maximize"));
        Add("Janela: centralizar", Window("center"));
        Add("Silenciar / reativar o som", ActionStep.Create(StepKind.Volume, ("mode", "mute")));
        Add("Volume +5%", ActionStep.Create(StepKind.Volume, ("mode", "up"), ("amount", "5")));
        Add("Volume −5%", ActionStep.Create(StepKind.Volume, ("mode", "down"), ("amount", "5")));
        Add("Mostrar a área de trabalho", ActionStep.Create(StepKind.System, ("command", "show-desktop")));
        Add("Bloco de Notas com a data de hoje",
            ActionStep.Create(StepKind.OpenApp, ("target", "notepad.exe")),
            ActionStep.Create(StepKind.Wait, ("ms", "900")),
            ActionStep.Create(StepKind.TypeText, ("text", "Anotações de {date}\n\n")));
    }

    // -------------------------------------------------------------- helpers

    private void Reload()
    {
        var list = new List<ActionDefinition>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT id, name, category, hotkey, scope_mode, scope_apps, enabled, on_error, steps, run_count, last_run, created, updated
                FROM actions ORDER BY name COLLATE NOCASE
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                List<ActionStep> steps;
                try { steps = JsonSerializer.Deserialize<List<ActionStep>>(r.GetString(8), StepsJson) ?? new(); }
                catch (JsonException ex)
                {
                    AppLog.Warn($"Action {r.GetInt64(0)} has unreadable steps: {ex.Message}");
                    steps = new();
                }
                list.Add(new ActionDefinition
                {
                    Id = r.GetInt64(0),
                    Name = r.GetString(1),
                    Category = r.GetString(2),
                    Hotkey = r.GetString(3),
                    Scope = (ScopeMode)r.GetInt32(4),
                    ScopeApps = r.GetString(5).Split('|', StringSplitOptions.RemoveEmptyEntries).ToList(),
                    Enabled = r.GetInt32(6) != 0,
                    OnError = (ErrorPolicy)r.GetInt32(7),
                    Steps = steps,
                    RunCount = r.GetInt32(9),
                    LastRunUtc = r.IsDBNull(10) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(10)).UtcDateTime,
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
