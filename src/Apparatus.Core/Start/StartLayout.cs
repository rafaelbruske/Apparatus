using System.Text.Json;
using System.Text.Json.Serialization;
using Apparatus.Core.Search;

namespace Apparatus.Core.Start;

/// <summary>Something pinned to the Start menu: anything the launcher can run, remembered with what it needs to run again.</summary>
public sealed class StartItem
{
    public string Key { get; set; } = "";
    public ResultKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Payload { get; set; } = "";
    public string? Icon { get; set; }

    public static StartItem From(SearchResult r) => new()
    {
        Key = r.Key, Kind = r.Kind, Title = r.Title, Subtitle = r.Subtitle, Payload = r.Payload, Icon = r.Icon,
    };

    public SearchResult ToResult() => new(Key, Kind, Title, Subtitle, Payload, 0, Icon);
}

/// <summary>A named group of pinned items ("Fixados", "Trabalho", "Jogos"...).</summary>
public sealed class StartSection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<StartItem> Items { get; set; } = new();
}

public sealed class StartLayout
{
    public int Version { get; set; } = 1;

    /// <summary>The first-run selection was made (so emptying the menu does not bring the defaults back).</summary>
    public bool Seeded { get; set; }

    public List<StartSection> Sections { get; set; } = new();

    /// <summary>Small shortcuts in the bottom bar (Settings, Documents, Downloads...).</summary>
    public List<StartItem> Quick { get; set; } = new();
}

/// <summary>
/// The Start menu's contents, saved as one JSON file. Every key is pinned at most once across all sections.
/// All changes raise <see cref="Changed"/> and are written to disk immediately.
/// </summary>
public sealed class StartLayoutStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public const string DefaultSectionName = "Fixados";
    public const int MaxQuick = 8;

    private readonly string _path;
    private readonly object _gate = new();
    private StartLayout _layout;

    public StartLayoutStore(string path)
    {
        _path = path;
        _layout = Load();
    }

    public event EventHandler? Changed;

    /// <summary>A deep copy: safe to enumerate while the store changes.</summary>
    public StartLayout Snapshot()
    {
        lock (_gate) return Clone(_layout);
    }

    public bool IsEmpty
    {
        get { lock (_gate) return _layout.Sections.All(s => s.Items.Count == 0); }
    }

    public bool Seeded
    {
        get { lock (_gate) return _layout.Seeded; }
    }

    /// <summary>What can live in the menu: things that exist outside the moment they were found.</summary>
    public static bool CanPin(SearchResult r) =>
        r.Kind is ResultKind.App or ResultKind.File or ResultKind.Folder or ResultKind.Setting or ResultKind.Url
            or ResultKind.Action or ResultKind.Command;

    public bool IsPinned(string key)
    {
        lock (_gate) return _layout.Sections.Any(s => s.Items.Any(i => Same(i.Key, key)));
    }

    // ----------------------------------------------------------------- items

    /// <summary>Pins to the given section (the first one when null). False when it is already pinned or cannot be.</summary>
    public bool Pin(SearchResult result, string? sectionId = null)
    {
        if (!CanPin(result)) return false;
        lock (_gate)
        {
            if (_layout.Sections.Any(s => s.Items.Any(i => Same(i.Key, result.Key)))) return false;
            var section = FindSection(sectionId) ?? EnsureFirstSection();
            section.Items.Add(StartItem.From(result));
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Unpin(string key)
    {
        lock (_gate)
        {
            bool removed = false;
            foreach (var s in _layout.Sections) removed |= s.Items.RemoveAll(i => Same(i.Key, key)) > 0;
            if (!removed) return false;
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Moves a pinned item to another section (at <paramref name="index"/>, or the end).</summary>
    public bool Move(string key, string toSectionId, int index = -1)
    {
        lock (_gate)
        {
            var target = FindSection(toSectionId);
            var source = _layout.Sections.FirstOrDefault(s => s.Items.Any(i => Same(i.Key, key)));
            if (target is null || source is null) return false;

            var item = source.Items.First(i => Same(i.Key, key));
            source.Items.Remove(item);
            target.Items.Insert(index < 0 || index > target.Items.Count ? target.Items.Count : index, item);
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Sets the order of a section's items (after a drag). Keys not listed keep their place at the end.</summary>
    public void SetOrder(string sectionId, IReadOnlyList<string> orderedKeys)
    {
        lock (_gate)
        {
            var section = FindSection(sectionId);
            if (section is null) return;
            var ordered = orderedKeys
                .Select(k => section.Items.FirstOrDefault(i => Same(i.Key, k)))
                .Where(i => i is not null).Cast<StartItem>().Distinct().ToList();
            ordered.AddRange(section.Items.Except(ordered));
            section.Items = ordered;
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // -------------------------------------------------------------- sections

    public string AddSection(string name)
    {
        string id;
        lock (_gate)
        {
            var s = new StartSection { Name = CleanName(name) };
            _layout.Sections.Add(s);
            Save();
            id = s.Id;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return id;
    }

    public void RenameSection(string id, string name)
    {
        lock (_gate)
        {
            if (FindSection(id) is not { } s) return;
            s.Name = CleanName(name);
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a section; its items go to the neighbouring one. The last section cannot be removed.</summary>
    public bool RemoveSection(string id)
    {
        lock (_gate)
        {
            int i = _layout.Sections.FindIndex(s => s.Id == id);
            if (i < 0 || _layout.Sections.Count < 2) return false;
            var heir = _layout.Sections[i == 0 ? 1 : i - 1];
            heir.Items.AddRange(_layout.Sections[i].Items.Where(x => !heir.Items.Any(h => Same(h.Key, x.Key))));
            _layout.Sections.RemoveAt(i);
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void MoveSection(string id, int delta)
    {
        lock (_gate)
        {
            int from = _layout.Sections.FindIndex(s => s.Id == id), to = from + delta;
            if (from < 0 || to < 0 || to >= _layout.Sections.Count) return;
            (_layout.Sections[from], _layout.Sections[to]) = (_layout.Sections[to], _layout.Sections[from]);
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ----------------------------------------------------------------- quick

    public bool AddQuick(SearchResult result)
    {
        if (!CanPin(result)) return false;
        lock (_gate)
        {
            if (_layout.Quick.Count >= MaxQuick || _layout.Quick.Any(q => Same(q.Key, result.Key))) return false;
            _layout.Quick.Add(StartItem.From(result));
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool RemoveQuick(string key)
    {
        lock (_gate)
        {
            if (_layout.Quick.RemoveAll(q => Same(q.Key, key)) == 0) return false;
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // ------------------------------------------------------------------ seed

    /// <summary>First run only: fills an empty menu with the given items and the quick shortcuts.</summary>
    public void SeedOnce(IEnumerable<SearchResult> pinned, IEnumerable<SearchResult> quick)
    {
        lock (_gate)
        {
            if (_layout.Seeded) return;
            _layout.Seeded = true;
            var first = EnsureFirstSection();
            foreach (var r in pinned.Where(CanPin))
            {
                if (!_layout.Sections.Any(s => s.Items.Any(i => Same(i.Key, r.Key)))) first.Items.Add(StartItem.From(r));
            }
            foreach (var r in quick.Where(CanPin).Take(MaxQuick)) _layout.Quick.Add(StartItem.From(r));
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ----------------------------------------------------------- backup/restore

    public string ExportJson()
    {
        lock (_gate) return JsonSerializer.Serialize(_layout, Json);
    }

    /// <summary>
    /// Replaces the whole layout with the one in <paramref name="json"/> (after cleaning it: no item without a key, no key
    /// pinned twice, no unnamed section, no more than <see cref="MaxQuick"/> quick items). Returns how many items were pinned.
    /// </summary>
    public int ImportJson(string json)
    {
        var imported = JsonSerializer.Deserialize<StartLayout>(json, Json) ?? throw new JsonException("layout vazio");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clean = new StartLayout { Seeded = true };
        foreach (var section in imported.Sections ?? new())
        {
            var s = new StartSection { Id = string.IsNullOrWhiteSpace(section.Id) ? Guid.NewGuid().ToString("N") : section.Id, Name = CleanName(section.Name ?? "") };
            foreach (var item in section.Items ?? new())
            {
                if (string.IsNullOrWhiteSpace(item.Key) || !seen.Add(item.Key)) continue;
                s.Items.Add(item);
            }
            clean.Sections.Add(s);
        }
        var quickKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in (imported.Quick ?? new()).Where(q => !string.IsNullOrWhiteSpace(q.Key) && quickKeys.Add(q.Key)).Take(MaxQuick))
            clean.Quick.Add(q);

        lock (_gate)
        {
            _layout = clean;
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return clean.Sections.Sum(s => s.Items.Count);
    }

    // --------------------------------------------------------------- helpers

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string CleanName(string name)
    {
        name = name.Trim();
        return name.Length == 0 ? "Sem nome" : name.Length > 40 ? name[..40] : name;
    }

    private StartSection? FindSection(string? id) => id is null ? null : _layout.Sections.FirstOrDefault(s => s.Id == id);

    private StartSection EnsureFirstSection()
    {
        if (_layout.Sections.Count == 0) _layout.Sections.Add(new StartSection { Name = DefaultSectionName });
        return _layout.Sections[0];
    }

    private static StartLayout Clone(StartLayout l) => JsonSerializer.Deserialize<StartLayout>(JsonSerializer.Serialize(l, Json), Json)!;

    private StartLayout Load()
    {
        try
        {
            if (File.Exists(_path)) return JsonSerializer.Deserialize<StartLayout>(File.ReadAllText(_path), Json) ?? new StartLayout();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            AppLog.Warn($"Start layout unreadable ({ex.Message}); starting from scratch");
            try { File.Copy(_path, _path + ".broken", overwrite: true); } catch (IOException) { }
        }
        return new StartLayout();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_layout, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException ex)
        {
            AppLog.Error("Saving the Start layout failed", ex);
        }
    }
}
