using Apparatus.Core;
using Velopack;

namespace Apparatus.App;

/// <summary>
/// Updates from a folder or web address that holds the releases written by the packer (Velopack). Nothing is downloaded unless
/// an update source is set, and nothing is installed without the user choosing to: a check only reports what is available.
/// A program that was not installed by the installer (run from a build folder) cannot update itself and says so.
/// </summary>
public sealed class UpdateService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    private readonly AppServices _services;
    private UpdateManager? _manager;
    private string _managerSource = "";
    private Timer? _timer;
    private UpdateInfo? _pending;

    public UpdateService(AppServices services) => _services = services;

    public string Source => _services.Settings.Current.UpdateSource.Trim();

    /// <summary>The version the installer put on this computer, or null when running from a build folder.</summary>
    public string? InstalledVersion => Manager()?.IsInstalled == true ? Manager()!.CurrentVersion?.ToString() : null;

    public bool IsInstalled => Manager()?.IsInstalled == true;

    /// <summary>The newest version found by the last check, if it is newer than this one.</summary>
    public string? AvailableVersion => _pending?.TargetFullRelease.Version.ToString();

    public string Status { get; private set; } = "";

    public event Action? StatusChanged;

    public void Start()
    {
        if (Source.Length == 0 || !IsInstalled) return;
        _timer = new Timer(async _ => await CheckAsync(notify: true).ConfigureAwait(false), null, TimeSpan.FromMinutes(2), CheckEvery);
    }

    private UpdateManager? Manager()
    {
        string source = Source;
        if (source.Length == 0) return null;
        if (_manager is not null && _managerSource == source) return _manager;
        try
        {
            _manager = new UpdateManager(source);
            _managerSource = source;
            return _manager;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Update source '{source}' is not usable: {ex.Message}");
            return null;
        }
    }

    /// <summary>Looks for a newer version. Returns its number, or null when there is none (or it could not be checked: see <see cref="Status"/>).</summary>
    public async Task<string?> CheckAsync(bool notify = false)
    {
        var manager = Manager();
        if (manager is null) return Fail("Escolha de onde vêm as atualizações.");
        if (!manager.IsInstalled) return Fail("Este Apparatus não foi instalado pelo instalador (está rodando de uma pasta de build), então não se atualiza sozinho.");

        try
        {
            _pending = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (_pending is null)
            {
                SetStatus($"Você está na versão mais recente ({manager.CurrentVersion}).");
                return null;
            }

            string version = _pending.TargetFullRelease.Version.ToString();
            SetStatus($"Versão {version} disponível.");
            if (notify)
                _services.Host.ShowBalloon("Atualização do Apparatus", $"A versão {version} está disponível. Instale em Configurações → Atualizações.");
            return version;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Checking for updates failed: {ex.Message}");
            return Fail($"Não foi possível verificar: {ex.Message}");
        }
    }

    /// <summary>Downloads the update found by the last check and restarts into it. Settings and data stay where they are.</summary>
    public async Task<bool> InstallAndRestartAsync()
    {
        var manager = Manager();
        if (manager is null || _pending is null) return false;
        try
        {
            SetStatus("Baixando a atualização…");
            await manager.DownloadUpdatesAsync(_pending, percent => SetStatus($"Baixando a atualização… {percent}%")).ConfigureAwait(false);
            SetStatus("Reiniciando para concluir…");
            manager.ApplyUpdatesAndRestart(_pending);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Installing the update failed", ex);
            Fail($"A atualização falhou: {ex.Message}");
            return false;
        }
    }

    private string? Fail(string message)
    {
        SetStatus(message);
        return null;
    }

    private void SetStatus(string text)
    {
        Status = text;
        AppLog.Info("Update: " + text);
        StatusChanged?.Invoke();
    }
}
