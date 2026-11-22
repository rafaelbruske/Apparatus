using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Apparatus.Core.Actions;
using Apparatus.Core.Settings;
using Apparatus.Core.Snippets;
using Apparatus.Core.Start;

namespace Apparatus.Core.Backup;

public sealed class BackupManifest
{
    public int Version { get; set; } = BackupBundle.CurrentVersion;

    /// <summary>Name of the computer that wrote the file (shown when importing on another one).</summary>
    public string Machine { get; set; } = "";

    public DateTime CreatedUtc { get; set; }

    public int Snippets { get; set; }

    public int Actions { get; set; }

    public int PinnedItems { get; set; }
}

/// <summary>What is inside a backup file, as text. Missing parts are null.</summary>
public sealed record BackupContent(BackupManifest Manifest, string? SettingsJson, string? SnippetsJson, string? ActionsJson, string? StartJson);

public sealed class BackupFormatException : Exception
{
    public BackupFormatException(string message) : base(message) { }
}

/// <summary>The backup file: a zip with one JSON entry per part. Nothing is ever extracted to disk, so a crafted file cannot write elsewhere.</summary>
public static class BackupBundle
{
    public const int CurrentVersion = 1;
    public const string Extension = ".apparatus";

    private const long MaxEntryBytes = 64L * 1024 * 1024;
    private const string ManifestEntry = "manifest.json", SettingsEntry = "settings.json", SnippetsEntry = "snippets.json",
        ActionsEntry = "actions.json", StartEntry = "start.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Write(Stream output, BackupContent content)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Add(zip, ManifestEntry, JsonSerializer.Serialize(content.Manifest, Json));
        if (content.SettingsJson is not null) Add(zip, SettingsEntry, content.SettingsJson);
        if (content.SnippetsJson is not null) Add(zip, SnippetsEntry, content.SnippetsJson);
        if (content.ActionsJson is not null) Add(zip, ActionsEntry, content.ActionsJson);
        if (content.StartJson is not null) Add(zip, StartEntry, content.StartJson);
    }

    public static BackupContent Read(Stream input)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new BackupFormatException("O arquivo não é um backup do Apparatus."); }

        using (zip)
        {
            string manifestText = ReadEntry(zip, ManifestEntry) ?? throw new BackupFormatException("O arquivo não é um backup do Apparatus (sem manifesto).");
            BackupManifest manifest;
            try { manifest = JsonSerializer.Deserialize<BackupManifest>(manifestText, Json) ?? throw new BackupFormatException("Manifesto vazio."); }
            catch (JsonException) { throw new BackupFormatException("O manifesto do backup está corrompido."); }

            if (manifest.Version > CurrentVersion)
                throw new BackupFormatException("Este backup foi feito por uma versão mais nova do Apparatus. Atualize o programa para importá-lo.");
            if (manifest.Version < 1) throw new BackupFormatException("Versão de backup desconhecida.");

            return new BackupContent(manifest, ReadEntry(zip, SettingsEntry), ReadEntry(zip, SnippetsEntry), ReadEntry(zip, ActionsEntry), ReadEntry(zip, StartEntry));
        }
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(new UTF8Encoding(false).GetBytes(text));
    }

    private static string? ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return null;
        if (entry.Length > MaxEntryBytes) throw new BackupFormatException($"“{name}” é grande demais para ser um backup válido.");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return reader.ReadToEnd();
    }
}

[Flags]
public enum BackupParts
{
    None = 0,
    Settings = 1,
    Snippets = 2,
    Actions = 4,
    StartLayout = 8,
    All = Settings | Snippets | Actions | StartLayout,
}

public sealed record BackupImportResult(
    bool SettingsApplied,
    SnippetStore.ImportResult? Snippets,
    ActionStore.ImportResult? Actions,
    int PinnedItems,
    IReadOnlyList<string> Problems);

/// <summary>Writes and reads the backup of everything the user configured. History, usage statistics and extension code are not part of it.</summary>
public sealed class BackupService
{
    private readonly SettingsStore _settings;
    private readonly SnippetStore _snippets;
    private readonly ActionStore _actions;
    private readonly StartLayoutStore _start;
    private readonly string _machine;

    public BackupService(SettingsStore settings, SnippetStore snippets, ActionStore actions, StartLayoutStore start, string? machineName = null)
    {
        _settings = settings;
        _snippets = snippets;
        _actions = actions;
        _start = start;
        _machine = machineName ?? Environment.MachineName;
    }

    public BackupContent Create(BackupParts parts = BackupParts.All)
    {
        string? snippets = parts.HasFlag(BackupParts.Snippets) ? _snippets.ExportJson() : null;
        string? actions = parts.HasFlag(BackupParts.Actions) ? _actions.ExportJson() : null;
        string? start = parts.HasFlag(BackupParts.StartLayout) ? _start.ExportJson() : null;
        string? settings = parts.HasFlag(BackupParts.Settings) ? SerializeSettings(ForExport(_settings.Current)) : null;

        return new BackupContent(new BackupManifest
        {
            Machine = _machine,
            CreatedUtc = DateTime.UtcNow,
            Snippets = _snippets.All.Count,
            Actions = _actions.All.Count,
            PinnedItems = _start.Snapshot().Sections.Sum(s => s.Items.Count),
        }, settings, snippets, actions, start);
    }

    public void Export(Stream output, BackupParts parts = BackupParts.All) => BackupBundle.Write(output, Create(parts));

    /// <summary>Applies the chosen parts of a backup. Snippets and actions are merged by name; settings and the Start layout are replaced.</summary>
    public BackupImportResult Import(BackupContent content, BackupParts parts)
    {
        var problems = new List<string>();
        bool settingsApplied = false;
        SnippetStore.ImportResult? snippets = null;
        ActionStore.ImportResult? actions = null;
        int pinned = 0;

        if (parts.HasFlag(BackupParts.Settings) && content.SettingsJson is not null)
        {
            try
            {
                var imported = JsonSerializer.Deserialize<AppSettings>(content.SettingsJson, SettingsStore.JsonOptions)
                    ?? throw new JsonException("vazio");
                _settings.Update(KeepMachineSpecific(imported, _settings.Current));
                settingsApplied = true;
            }
            catch (JsonException ex)
            {
                problems.Add($"Configurações ilegíveis: {ex.Message}");
            }
        }

        if (parts.HasFlag(BackupParts.Snippets) && content.SnippetsJson is not null)
        {
            try { snippets = _snippets.ImportJson(content.SnippetsJson); }
            catch (Exception ex) when (ex is JsonException or SnippetValidationException) { problems.Add($"Snippets ilegíveis: {ex.Message}"); }
        }

        if (parts.HasFlag(BackupParts.Actions) && content.ActionsJson is not null)
        {
            try { actions = _actions.ImportJson(content.ActionsJson); }
            catch (Exception ex) when (ex is JsonException or ActionValidationException) { problems.Add($"Ações ilegíveis: {ex.Message}"); }
        }

        if (parts.HasFlag(BackupParts.StartLayout) && content.StartJson is not null)
        {
            try { pinned = _start.ImportJson(content.StartJson); }
            catch (JsonException ex) { problems.Add($"Menu Iniciar ilegível: {ex.Message}"); }
        }

        return new BackupImportResult(settingsApplied, snippets, actions, pinned, problems);
    }

    // --------------------------------------------------------------- settings

    /// <summary>What does not travel between computers: the startup entry, the folders to index and the extension switches.</summary>
    internal static AppSettings ForExport(AppSettings current)
    {
        var copy = current.Clone();
        copy.RunAtStartup = false;
        copy.IndexRoots = null;
        copy.SyncFolder = "";
        copy.UpdateSource = "";
        copy.EnabledExtensions = new();
        copy.GlobalExtensions = new();
        return copy;
    }

    /// <summary>
    /// Settings from a backup, except what must stay a decision of this computer: the launcher hotkey (it may be taken here),
    /// startup, the indexed folders, the clipboard history and Windows-key switches (they change how the machine behaves)
    /// and which extensions run.
    /// </summary>
    internal static AppSettings KeepMachineSpecific(AppSettings imported, AppSettings current)
    {
        imported.Hotkey = current.Hotkey;
        imported.RunAtStartup = current.RunAtStartup;
        imported.IndexRoots = current.IndexRoots;
        imported.SyncFolder = current.SyncFolder;
        imported.UpdateSource = current.UpdateSource;
        imported.ClipboardHistory = current.ClipboardHistory;
        imported.StartMenuEnabled = current.StartMenuEnabled;
        imported.EnabledExtensions = current.EnabledExtensions;
        imported.GlobalExtensions = current.GlobalExtensions;
        imported.ExtensionSampleInstalled = current.ExtensionSampleInstalled;
        return imported;
    }

    private static string SerializeSettings(AppSettings settings) => JsonSerializer.Serialize(settings, SettingsStore.JsonOptions);
}
