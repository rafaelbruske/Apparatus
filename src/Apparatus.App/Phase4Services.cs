using System.Text.Json;
using Apparatus.Core;
using Apparatus.Core.Actions;
using Apparatus.Core.Extensions;
using Apparatus.Native;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace Apparatus.App;

/// <summary>Records what the user copies (when switched on) and pastes entries back from the launcher.</summary>
public sealed class ClipboardService : IDisposable
{
    private readonly AppServices _services;
    private readonly ClipboardAccess _access = new();
    private readonly Timer _pruneTimer;
    private long _copyCounter;

    public ClipboardService(AppServices services)
    {
        _services = services;
        services.Host.ClipboardChanged += OnClipboardChanged;
        services.Settings.Changed += (_, _) => ApplySettings();
        ApplySettings();
        _pruneTimer = new Timer(_ => _services.ClipboardStore.Prune(), null, TimeSpan.FromMinutes(5), TimeSpan.FromHours(6));
    }

    public bool Enabled => _services.Settings.Current.ClipboardHistory;

    public void ApplySettings()
    {
        var s = _services.Settings.Current;
        _services.ClipboardStore.MaxEntries = Math.Clamp(s.ClipboardMaxEntries, 10, 10_000);
        _services.ClipboardStore.RetentionDays = Math.Clamp(s.ClipboardRetentionDays, 1, 3650);
    }

    private void OnClipboardChanged()
    {
        if (!Enabled || !_services.ClipboardStore.IsAvailable) return;
        long ticket = Interlocked.Increment(ref _copyCounter);
        var excluded = _services.Settings.Current.ClipboardExcludedProcesses.ToList();

        Task.Run(async () =>
        {
            // Programs often update the clipboard several times in a row while copying: keep only the last one.
            await Task.Delay(80).ConfigureAwait(false);
            if (ticket != Interlocked.Read(ref _copyCounter)) return;
            try
            {
                var capture = _access.ReadForHistory(excluded);
                if (capture is not null) _services.ClipboardStore.Add(capture.Text, capture.Source);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Reading the clipboard for the history failed: {ex.Message}");
            }
        });
    }

    /// <summary>Enter pastes the entry into the program that was in front; Ctrl+Enter only puts it back on the clipboard.</summary>
    public async void Activate(long id, bool copyOnly, nint target)
    {
        try
        {
            var entry = _services.ClipboardStore.Get(id);
            if (entry is null || _services.Expander is null) return;
            await _services.Expander.InsertPlainAsync(entry.Text, target, copyOnly);
        }
        catch (Exception ex)
        {
            AppLog.Error("Pasting from the clipboard history failed", ex);
            _services.Host.ShowBalloon("Não foi possível colar", ex.Message);
        }
    }

    public bool TogglePin(long id)
    {
        var entry = _services.ClipboardStore.Get(id);
        if (entry is null) return false;
        _services.ClipboardStore.SetPinned(id, !entry.Pinned);
        return !entry.Pinned;
    }

    public void Dispose()
    {
        _pruneTimer.Dispose();
        _access.Dispose();
    }
}

/// <summary>Carries out what an extension result asks for: open, copy, insert, reveal or call the extension back.</summary>
public sealed class ExtensionRunner
{
    private readonly AppServices _services;
    private readonly DispatcherQueue _ui;

    public ExtensionRunner(AppServices services, DispatcherQueue ui)
    {
        _services = services;
        _ui = ui;
    }

    public async void Run(string payloadJson, bool secondary, nint target)
    {
        try
        {
            var payload = ExtPayload.FromJson(payloadJson) ?? throw new InvalidOperationException("Resultado de extensão ilegível.");
            var action = (secondary ? payload.Alt ?? payload.A : payload.A) ?? new ExtAction { Type = "callback" };

            switch (action.Type.ToLowerInvariant())
            {
                case "none":
                    break;
                case "open":
                    ShellLauncher.Open(action.Target ?? throw new InvalidOperationException("A ação “open” precisa de “target”."));
                    break;
                case "reveal":
                    ShellLauncher.RevealInExplorer(action.Path ?? action.Target ?? throw new InvalidOperationException("A ação “reveal” precisa de “path”."));
                    break;
                case "copy":
                    Copy(action.Text ?? throw new InvalidOperationException("A ação “copy” precisa de “text”."));
                    break;
                case "insert":
                    if (_services.Expander is { } expander)
                        await expander.InsertPlainAsync(action.Text ?? throw new InvalidOperationException("A ação “insert” precisa de “text”."), target);
                    break;
                case "callback":
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                        await _services.Extensions.ActivateAsync(payload.E, payload.R, action.Data, cts.Token);
                    break;
                default:
                    throw new InvalidOperationException($"Ação desconhecida: “{action.Type}”.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Extension action failed: {ex.Message}");
            _services.Host.ShowBalloon("A extensão não conseguiu concluir", ex.Message);
        }
    }

    private void Copy(string text) => _ui.TryEnqueue(() =>
    {
        var package = new DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
    });

    // -------------------------------------------------------------- samples

    /// <summary>Copies the bundled sample extension next to the user's own, once. It starts switched off.</summary>
    public static void InstallSampleOnce(AppServices services)
    {
        if (services.Settings.Current.ExtensionSampleInstalled) return;
        try
        {
            CopySample(services.Extensions.Folder);
            services.ApplySettings(s => s.ExtensionSampleInstalled = true);
            services.Extensions.Reload();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Installing the sample extension failed: {ex.Message}");
        }
    }

    /// <summary>Copies (or restores) the sample into the extensions folder. Existing files are overwritten.</summary>
    public static void CopySample(string extensionsFolder)
    {
        string from = Path.Combine(AppContext.BaseDirectory, "Samples", "hello-powershell");
        string to = Path.Combine(extensionsFolder, "hello-powershell");
        if (!Directory.Exists(from)) throw new DirectoryNotFoundException("A extensão de exemplo não acompanha esta instalação.");
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
    }
}

/// <summary>The "open the launcher with this text" action step.</summary>
public sealed class LauncherStepExecutor : IStepExecutor
{
    private readonly AppServices _services;

    public LauncherStepExecutor(AppServices services) => _services = services;

    public bool CanExecute(StepKind kind) => kind == StepKind.OpenLauncher;

    public Task ExecuteAsync(ActionStep step, CancellationToken ct)
    {
        _services.ShowLauncher?.Invoke(step.Get("query"));
        return Task.CompletedTask;
    }
}
