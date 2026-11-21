using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apparatus.Core.Settings;

public sealed class WebSearchEngine
{
    public string Keyword { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>URL with a {query} placeholder.</summary>
    public string UrlTemplate { get; set; } = "";
}

public sealed class AppSettings
{
    /// <summary>Global hotkey that opens the launcher. Configurable in the settings window.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";

    public bool RunAtStartup { get; set; }

    public int MaxResults { get; set; } = 8;

    public bool IndexFiles { get; set; } = true;

    /// <summary>Folders to index. Null means automatic: user folders plus every non-system fixed drive.</summary>
    public List<string>? IndexRoots { get; set; }

    /// <summary>Directory names that are never indexed or descended into.</summary>
    public List<string> ExcludedNames { get; set; } = new()
    {
        "node_modules", ".git", ".svn", ".hg", ".vs", ".idea", "__pycache__",
        "$RECYCLE.BIN", "System Volume Information", "AppData", "Windows.old",
    };

    /// <summary>Safety valve so a huge drive cannot exhaust memory.</summary>
    public int MaxEntriesPerRoot { get; set; } = 3_000_000;

    /// <summary>Expand snippet triggers while typing, in any application.</summary>
    public bool SnippetExpansion { get; set; } = true;

    /// <summary>Process names (without .exe) in which snippets never expand: password managers, remote desktop...</summary>
    public List<string> SnippetExcludedProcesses { get; set; } = new()
    {
        "KeePass", "KeePassXC", "1Password", "Bitwarden", "Dashlane", "LastPass", "mstsc", "vmconnect",
    };

    /// <summary>Keyword of the engine used when nothing matches (see <see cref="SearchEngines"/>).</summary>
    /// <summary>Keep a history of copied text (opt-in: it records everything copied, so it starts switched off).</summary>
    public bool ClipboardHistory { get; set; }

    public int ClipboardMaxEntries { get; set; } = 500;

    public int ClipboardRetentionDays { get; set; } = 30;

    /// <summary>Copies made in these programs are never recorded.</summary>
    public List<string> ClipboardExcludedProcesses { get; set; } = new()
    {
        "KeePass", "KeePassXC", "1Password", "Bitwarden", "Dashlane", "LastPass", "mstsc",
    };

    /// <summary>Ids of the extensions the user switched on. A new extension never runs until it is listed here.</summary>
    public List<string> EnabledExtensions { get; set; } = new();

    /// <summary>Enabled extensions that also receive ordinary searches (not only their keyword).</summary>
    public List<string> GlobalExtensions { get; set; } = new();

    /// <summary>Set once the bundled sample extension has been copied to the extensions folder.</summary>
    public bool ExtensionSampleInstalled { get; set; }

    public string DefaultSearchEngine { get; set; } = "g";

    public List<WebSearchEngine> SearchEngines { get; set; } = DefaultEngines();

    public static List<WebSearchEngine> DefaultEngines() => new()
    {
        new() { Keyword = "g", Name = "Google", UrlTemplate = "https://www.google.com/search?q={query}" },
        new() { Keyword = "yt", Name = "YouTube", UrlTemplate = "https://www.youtube.com/results?search_query={query}" },
        new() { Keyword = "w", Name = "Wikipédia", UrlTemplate = "https://pt.wikipedia.org/w/index.php?search={query}" },
        new() { Keyword = "gh", Name = "GitHub", UrlTemplate = "https://github.com/search?q={query}" },
        new() { Keyword = "maps", Name = "Google Maps", UrlTemplate = "https://www.google.com/maps/search/{query}" },
    };

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
}

/// <summary>Loads and saves settings as JSON. Writes are atomic (temp file + move).</summary>
public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private AppSettings _current;

    public SettingsStore(string path)
    {
        _path = path;
        _current = Load();
    }

    public event EventHandler<AppSettings>? Changed;

    /// <summary>A snapshot; mutate a <see cref="AppSettings.Clone"/> and pass it to <see cref="Update"/>.</summary>
    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public void Update(AppSettings settings)
    {
        lock (_gate)
        {
            _current = settings;
            Save(settings);
        }
        Changed?.Invoke(this, settings);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                using var stream = File.OpenRead(_path);
                return JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to read settings; using defaults", ex);
            try { File.Copy(_path, _path + ".broken", overwrite: true); } catch { /* best effort */ }
        }
        return new AppSettings();
    }

    private void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to save settings", ex);
        }
    }
}
