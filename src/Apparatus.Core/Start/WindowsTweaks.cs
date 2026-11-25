using System.Text.Json;

namespace Apparatus.Core.Start;

/// <summary>Where the values live. Implemented on top of the real registry (and, in tests, an in-memory dictionary).</summary>
public interface ITweakRegistry
{
    int? ReadDword(string keyPath, string valueName);

    /// <summary>Throws <see cref="UnauthorizedAccessException"/> or <see cref="IOException"/> when Windows refuses.</summary>
    void WriteDword(string keyPath, string valueName, int value);

    void DeleteValue(string keyPath, string valueName);
}

public sealed record TweakOption(int Value, string Label);

/// <summary>One adjustable setting of the taskbar or of the Windows Start menu, stored as a DWORD under HKCU.</summary>
public sealed record WindowsTweak(
    string Id,
    string Title,
    string Description,
    string KeyPath,
    string ValueName,
    IReadOnlyList<TweakOption> Options,
    int DefaultValue,
    /// <summary>Other value names that must hold the same number (e.g. the multi-monitor variant of a setting).</summary>
    IReadOnlyList<string>? MirrorValueNames = null,
    /// <summary>Takes effect only after Explorer is restarted (or the user signs in again).</summary>
    bool NeedsExplorerRestart = true,
    /// <summary>Newer Windows builds protect some of these values; the write may be refused or ignored.</summary>
    string? Caveat = null);

public sealed record TweakResult(bool Success, string Message, bool NeedsExplorerRestart);

/// <summary>The catalogue of tweaks the app offers. All live under the current user's hive.</summary>
public static class TweakCatalog
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Search = @"Software\Microsoft\Windows\CurrentVersion\Search";

    private static readonly TweakOption[] OffOn = { new(0, "Desligado"), new(1, "Ligado") };

    public static IReadOnlyList<WindowsTweak> All { get; } = new WindowsTweak[]
    {
        new("taskbar-alignment", "Alinhamento dos ícones da barra de tarefas", "Esquerda (como no Windows 10) ou centro.",
            Advanced, "TaskbarAl", new TweakOption[] { new(0, "Esquerda"), new(1, "Centro") }, DefaultValue: 1, NeedsExplorerRestart: false),
        new("taskbar-size", "Tamanho da barra de tarefas", "Pequena, média ou grande.",
            Advanced, "TaskbarSi", new TweakOption[] { new(0, "Pequena"), new(1, "Média"), new(2, "Grande") }, DefaultValue: 1,
            Caveat: "Algumas versões do Windows 11 ignoram este valor."),
        new("search-box", "Pesquisa na barra de tarefas", "Esconde a pesquisa ou escolhe como ela aparece.",
            Search, "SearchboxTaskbarMode",
            new TweakOption[] { new(0, "Oculta"), new(1, "Só o ícone"), new(2, "Caixa de pesquisa"), new(3, "Ícone e texto") }, DefaultValue: 1),
        new("task-view", "Botão de Visão de Tarefas", "Mostra ou esconde o botão das áreas de trabalho virtuais.",
            Advanced, "ShowTaskViewButton", OffOn, DefaultValue: 1),
        new("widgets", "Botão de Widgets", "Mostra ou esconde o botão de Widgets.",
            Advanced, "TaskbarDa", OffOn, DefaultValue: 1,
            Caveat: "O Windows protege este valor em versões recentes; se a escolha não for aceita, use Configurações → Personalização → Barra de tarefas."),
        new("combine-buttons", "Agrupar botões da barra de tarefas", "Quando juntar as janelas do mesmo programa.",
            Advanced, "TaskbarGlomLevel", new TweakOption[] { new(0, "Sempre"), new(1, "Quando a barra estiver cheia"), new(2, "Nunca") },
            DefaultValue: 0, MirrorValueNames: new[] { "MMTaskbarGlomLevel" }),
        new("clock-seconds", "Segundos no relógio", "Mostra os segundos no relógio da barra de tarefas.",
            Advanced, "ShowSecondsInSystemClock", OffOn, DefaultValue: 0),
        new("start-layout", "Layout do Iniciar do Windows", "Para quem continuar usando o menu Iniciar original: mais itens fixados ou mais recomendações.",
            Advanced, "Start_Layout", new TweakOption[] { new(0, "Padrão"), new(1, "Mais itens fixados"), new(2, "Mais recomendações") }, DefaultValue: 0),
        new("start-recent", "Arquivos recentes no Iniciar do Windows", "Mostra os documentos abertos recentemente no menu Iniciar original.",
            Advanced, "Start_TrackDocs", OffOn, DefaultValue: 1),
    };

    /// <summary>Id of the Windows option "use the Print Screen key to open screen capture". The Apparatus screen capture turns it off while it owns that key.</summary>
    public const string PrintScreenSnippingId = "printscreen-snipping";

    /// <summary>Tweaks the program changes by itself for a feature; they are not offered on the tweaks page and "restore all" leaves them alone.</summary>
    public static IReadOnlyList<WindowsTweak> Internal { get; } = new WindowsTweak[]
    {
        new(PrintScreenSnippingId, "Print Screen abre a Ferramenta de Captura", "Opção do Windows em Configurações → Acessibilidade → Teclado.",
            @"Control Panel\Keyboard", "PrintScreenKeyForSnippingEnabled", OffOn, DefaultValue: 1, NeedsExplorerRestart: false),
    };

    public static WindowsTweak Get(string id) =>
        All.Concat(Internal).FirstOrDefault(t => t.Id == id) ?? throw new ArgumentException($"Ajuste desconhecido: {id}");
}

/// <summary>
/// Reads and changes the tweaks, remembering the original value of each the first time it is touched so every
/// change can be undone exactly (including "the value did not exist"). Backups survive restarts.
/// </summary>
public sealed class TweakService
{
    private sealed class Backup
    {
        /// <summary>Original value per tweak id; null means the value did not exist.</summary>
        public Dictionary<string, int?> Originals { get; set; } = new();

        /// <summary>The same for mirror value names, keyed "id/name".</summary>
        public Dictionary<string, int?> MirrorOriginals { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly ITweakRegistry _registry;
    private readonly string _backupPath;
    private readonly object _gate = new();
    private Backup _backup;

    public TweakService(ITweakRegistry registry, string backupPath)
    {
        _registry = registry;
        _backupPath = backupPath;
        _backup = LoadBackup();
    }

    /// <summary>The value Windows is using now (its default when the value is absent).</summary>
    public int Current(string id)
    {
        var t = TweakCatalog.Get(id);
        return _registry.ReadDword(t.KeyPath, t.ValueName) ?? t.DefaultValue;
    }

    public bool HasBackup(string id)
    {
        lock (_gate) return _backup.Originals.ContainsKey(id);
    }

    public bool IsChanged(string id) => HasBackup(id) && Current(id) != Original(id);

    private int Original(string id)
    {
        var t = TweakCatalog.Get(id);
        lock (_gate) return _backup.Originals.TryGetValue(id, out var v) ? v ?? t.DefaultValue : t.DefaultValue;
    }

    public TweakResult Apply(string id, int value)
    {
        var t = TweakCatalog.Get(id);
        if (t.Options.All(o => o.Value != value)) return new TweakResult(false, "Valor inválido para este ajuste.", false);

        lock (_gate)
        {
            // Remember what was there the first time only: later changes must not overwrite the true original.
            if (!_backup.Originals.ContainsKey(id))
            {
                _backup.Originals[id] = _registry.ReadDword(t.KeyPath, t.ValueName);
                foreach (var mirror in t.MirrorValueNames ?? Array.Empty<string>())
                    _backup.MirrorOriginals[$"{id}/{mirror}"] = _registry.ReadDword(t.KeyPath, mirror);
                SaveBackup();
            }
        }

        try
        {
            _registry.WriteDword(t.KeyPath, t.ValueName, value);
            foreach (var mirror in t.MirrorValueNames ?? Array.Empty<string>()) _registry.WriteDword(t.KeyPath, mirror, value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            AppLog.Warn($"Tweak '{id}' refused by Windows: {ex.Message}");
            return new TweakResult(false, "O Windows recusou a alteração" + (t.Caveat is null ? "." : $". {t.Caveat}"), false);
        }

        // Verify: some values are silently reset or ignored by newer builds.
        if (_registry.ReadDword(t.KeyPath, t.ValueName) != value)
            return new TweakResult(false, "O Windows não manteve o valor." + (t.Caveat is null ? "" : $" {t.Caveat}"), false);

        return new TweakResult(true, t.NeedsExplorerRestart ? "Salvo. Reinicie o Explorer para ver o efeito." : "Aplicado.", t.NeedsExplorerRestart);
    }

    /// <summary>Puts the value back exactly as it was before the first change (deleting it if it did not exist).</summary>
    public TweakResult Restore(string id)
    {
        var t = TweakCatalog.Get(id);
        int? original;
        Dictionary<string, int?> mirrors;
        lock (_gate)
        {
            if (!_backup.Originals.TryGetValue(id, out original)) return new TweakResult(true, "Já está como o Windows deixou.", false);
            mirrors = _backup.MirrorOriginals.Where(kv => kv.Key.StartsWith(id + "/", StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key[(id.Length + 1)..], kv => kv.Value);
        }

        try
        {
            Put(t.KeyPath, t.ValueName, original);
            foreach (var (name, value) in mirrors) Put(t.KeyPath, name, value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return new TweakResult(false, "O Windows recusou a restauração: " + ex.Message, false);
        }

        lock (_gate)
        {
            _backup.Originals.Remove(id);
            foreach (var key in _backup.MirrorOriginals.Keys.Where(k => k.StartsWith(id + "/", StringComparison.Ordinal)).ToList())
                _backup.MirrorOriginals.Remove(key);
            SaveBackup();
        }
        return new TweakResult(true, "Restaurado.", t.NeedsExplorerRestart);
    }

    /// <summary>Restores every tweak that was changed. Returns how many were put back.</summary>
    public int RestoreAll()
    {
        List<string> ids;
        lock (_gate) ids = _backup.Originals.Keys.Where(id => TweakCatalog.All.Any(t => t.Id == id)).ToList();
        return ids.Count(id => Restore(id).Success);
    }

    private void Put(string keyPath, string name, int? value)
    {
        if (value is { } v) _registry.WriteDword(keyPath, name, v);
        else _registry.DeleteValue(keyPath, name);
    }

    private Backup LoadBackup()
    {
        try
        {
            if (File.Exists(_backupPath)) return JsonSerializer.Deserialize<Backup>(File.ReadAllText(_backupPath), Json) ?? new Backup();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            AppLog.Warn($"Tweak backup unreadable: {ex.Message}");
        }
        return new Backup();
    }

    private void SaveBackup()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_backupPath))!);
            string temp = _backupPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_backup, Json));
            File.Move(temp, _backupPath, overwrite: true);
        }
        catch (IOException ex)
        {
            AppLog.Error("Saving the tweak backup failed", ex);
        }
    }
}
