using System.Text.Json;
using Apparatus.Core;
using Apparatus.Core.Backup;

namespace Apparatus.App;

/// <summary>
/// Keeps a backup file in the sync folder up to date (the folder is synchronised by OneDrive, Dropbox or similar, so the same
/// file reaches the other computers). It never overwrites a newer file written by another computer before this one has seen it,
/// and it never imports by itself: a newer file from elsewhere only produces a notice, and the import is the user's decision.
/// </summary>
public sealed class SyncService : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupCheckDelay = TimeSpan.FromSeconds(20);

    private readonly AppServices _services;
    private readonly BackupService _backup;
    private readonly string _machine = Environment.MachineName;
    private readonly object _gate = new();
    private readonly Timer _writeTimer;
    private Timer? _startupTimer;
    private DateTime _seenUtc;
    private bool _conflictNotified;

    public SyncService(AppServices services, BackupService backup)
    {
        _services = services;
        _backup = backup;
        _seenUtc = LoadSeen();
        _writeTimer = new Timer(_ => WriteNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Human-readable result of the last thing the service did (shown in the settings).</summary>
    public string Status { get; private set; } = "";

    public event Action? StatusChanged;

    public string? Folder => _services.Settings.Current.SyncFolder is { Length: > 0 } f ? f : null;

    public string? FilePath => Folder is { } f ? Path.Combine(f, SyncPolicy.FileName) : null;

    public void Start()
    {
        _services.Snippets.Changed += (_, _) => Schedule();
        _services.Actions.Changed += (_, _) => Schedule();
        _services.StartLayout.Changed += (_, _) => Schedule();
        _services.Settings.Changed += (_, _) => Schedule();
        _startupTimer = new Timer(_ => CheckIncoming(), null, StartupCheckDelay, Timeout.InfiniteTimeSpan);
        Schedule();   // makes sure a backup exists (or is brought up to date) after starting
    }

    private void Schedule()
    {
        if (Folder is null) return;
        _writeTimer.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    // ------------------------------------------------------------- writing

    /// <summary>Writes the backup now (also used by the "Sincronizar agora" button). Returns true when the file was written.</summary>
    public bool WriteNow()
    {
        lock (_gate)
        {
            if (FilePath is not { } path) return false;
            try
            {
                Directory.CreateDirectory(Folder!);
                var existing = ReadManifest(path);
                if (SyncPolicy.BeforeWrite(existing, _machine, _seenUtc) == SyncDecision.HoldBackConflict)
                {
                    SetStatus($"Não gravei: a pasta tem alterações mais novas feitas em {existing!.Machine}. Importe-as primeiro.");
                    if (!_conflictNotified)
                    {
                        _conflictNotified = true;
                        _services.Host.ShowBalloon("Sincronização em espera",
                            $"Há alterações mais novas de {existing.Machine} na pasta de sincronização. Importe-as em Configurações → Backup antes de continuar.");
                    }
                    return false;
                }

                var content = _backup.Create();
                string temp = path + ".tmp";
                using (var stream = File.Create(temp)) BackupBundle.Write(stream, content);
                File.Move(temp, path, overwrite: true);

                _seenUtc = content.Manifest.CreatedUtc;
                SaveSeen();
                _conflictNotified = false;
                SetStatus($"Gravado em {DateTime.Now:HH:mm:ss}.");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Writing the sync file failed: {ex.Message}");
                SetStatus($"Não foi possível gravar na pasta de sincronização: {ex.Message}");
                return false;
            }
        }
    }

    // ------------------------------------------------------------- reading

    /// <summary>The manifest of the file in the sync folder, or null when there is none or it cannot be read.</summary>
    public BackupManifest? PeekFolderFile()
    {
        lock (_gate) return FilePath is { } p ? ReadManifest(p) : null;
    }

    /// <summary>True when the folder holds a file from another computer that this one has not seen.</summary>
    public bool HasUnseenChanges()
    {
        var manifest = PeekFolderFile();
        return manifest is not null && SyncPolicy.IsUnseenFromElsewhere(manifest, _machine, _seenUtc);
    }

    private void CheckIncoming()
    {
        try
        {
            if (!HasUnseenChanges()) return;
            var m = PeekFolderFile()!;
            _services.Host.ShowBalloon("Há uma versão mais nova na pasta de sincronização",
                $"Feita em {m.Machine}, {m.CreatedUtc.ToLocalTime():dd/MM HH:mm}. Para usá-la: Configurações → Backup → Importar da pasta.");
            SetStatus($"Há alterações mais novas de {m.Machine} na pasta (ainda não importadas).");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Checking the sync folder failed: {ex.Message}");
        }
    }

    /// <summary>Imports the parts of the folder's file that the user chose. Marks that file as seen.</summary>
    public BackupImportResult? ImportFromFolder(BackupParts parts)
    {
        lock (_gate)
        {
            if (FilePath is not { } path || !File.Exists(path)) return null;
            BackupContent content;
            using (var stream = File.OpenRead(path)) content = BackupBundle.Read(stream);

            var result = _backup.Import(content, parts);
            _seenUtc = content.Manifest.CreatedUtc;
            SaveSeen();
            _conflictNotified = false;
            SetStatus($"Importado de {content.Manifest.Machine} em {DateTime.Now:HH:mm:ss}.");
            return result;
        }
    }

    private static BackupManifest? ReadManifest(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return BackupBundle.Read(stream).Manifest;
        }
        catch (Exception ex) when (ex is IOException or BackupFormatException or UnauthorizedAccessException)
        {
            return null;   // unreadable (still syncing, or not ours): treated as absent, never as a reason to fail
        }
    }

    // --------------------------------------------------------------- state

    private sealed class State
    {
        public DateTime SeenUtc { get; set; }
    }

    private static DateTime LoadSeen()
    {
        try
        {
            if (File.Exists(AppPaths.SyncStateFile))
                return JsonSerializer.Deserialize<State>(File.ReadAllText(AppPaths.SyncStateFile))?.SeenUtc ?? DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return DateTime.MinValue;
    }

    private void SaveSeen()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SyncStateFile)!);
            File.WriteAllText(AppPaths.SyncStateFile, JsonSerializer.Serialize(new State { SeenUtc = _seenUtc }));
        }
        catch (IOException ex)
        {
            AppLog.Warn($"Saving the sync state failed: {ex.Message}");
        }
    }

    private void SetStatus(string text)
    {
        Status = text;
        AppLog.Info("Sync: " + text);
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _writeTimer.Dispose();
        _startupTimer?.Dispose();
    }
}
