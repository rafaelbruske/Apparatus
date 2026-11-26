using Apparatus.Core;
using Apparatus.Core.Input;
using Apparatus.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Apparatus.App;

public partial class App : Application
{
    private const int CmdOpen = 1, CmdSettings = 2, CmdAutostart = 3, CmdReindex = 4, CmdQuit = 5, CmdSnippets = 6, CmdExpansion = 7, CmdActions = 8, CmdClipboard = 9, CmdStart = 10, CmdStartKey = 11, CmdCapture = 12;

    private readonly SingleInstance _instance;
    private readonly string[] _args;
    private DispatcherQueue _ui = null!;
    private MessageHost? _host;
    private AppServices? _services;
    private LauncherWindow? _launcher;
    private SettingsWindow? _settings;

    public App(SingleInstance instance, string[] args)
    {
        _instance = instance;
        _args = args;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            AppLog.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;   // a failed render must not take the resident app down
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { AppLog.Error("Unobserved task exception", e.Exception); e.SetObserved(); };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ui = DispatcherQueue.GetForCurrentThread();

        _host = new MessageHost();
        _services = new AppServices(_host);
        _services.Expander = new SnippetService(_services, _ui);
        _services.ActionHost = new ActionService(_services, _ui);
        _services.ExtensionRunner = new ExtensionRunner(_services, _ui);
        _services.ClipboardHost = new ClipboardService(_services);
        _services.CaptureHost = new CaptureService(_services, _ui);
        var executor = new ResultExecutor(_services, page => OpenSettings(page), Quit, newSnippet => OpenSnippets(newSnippet), newAction => OpenActions(newAction),
            draft => OpenActions(true, draft), draft => OpenSnippets(true, draft));
        _launcher = new LauncherWindow(_services, executor) { SettingsRequested = page => OpenSettings(page) };
        _services.ShowLauncher = query => _ui.TryEnqueue(() => _launcher.ShowLauncher(string.IsNullOrEmpty(query) ? null : query));
        _services.StartHost = new StartMenuService(_services, _ui, executor);
        _services.StartHost.Prewarm(done: () =>
        {
            if (_services.StartHost is not { } start) return;
            if (ArgValue("--start-query") is { } q) start.ShowWithQuery(q);
            else if (_args.Contains("--start-all", StringComparer.OrdinalIgnoreCase)) start.ShowAllApps();
            else if (_args.Contains("--start", StringComparer.OrdinalIgnoreCase)) start.Show();
        });

        _host.TrayMenuProvider = BuildTrayMenu;
        _host.TrayClicked += () => _launcher.ShowLauncher();
        _host.TrayCommand += OnTrayCommand;
        _host.HotkeyPressed += id =>
        {
            // While a macro is being recorded the settings window is hidden on purpose, and the hotkey must not bring the launcher up.
            if (id == MessageHost.LauncherHotkeyId && _settings?.IsRecordingMacro != true) _launcher.ToggleLauncher();
        };
        _host.ShowTrayIcon(AppPaths.AssetPath("apparatus.ico"), "Apparatus");

        var registration = _services.RegisterConfiguredHotkey();
        var capture = _services.CaptureHost.Restore();
        if (!capture.Success) _host.ShowBalloon("Captura de tela indisponível", capture.Message);
        _services.StartBackgroundWork();
        try
        {
            if (AutoStart.Reconcile(_services.Settings.Current.RunAtStartup)) AppLog.Info("Start-up entry was missing or outdated: rewritten");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Checking the start-up entry failed: {ex.Message}");
        }
        _services.Sync.Start();
        _services.Updates.Start();
        FreezeUiForTesting();
        if (_args.Contains("--update-now", StringComparer.OrdinalIgnoreCase)) _ = UpdateNowAsync();
        ExtensionRunner.InstallSampleOnce(_services);
        _instance.Listen(() => _ui.TryEnqueue(() => _launcher.ShowLauncher()));

        bool background = _args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        _launcher.Prewarm(done: () =>
        {
            if (!registration.Success)
            {
                string hotkey = _services.Settings.Current.Hotkey;
                _host.ShowBalloon("Atalho indisponível",
                    $"{hotkey} já está em uso por outro aplicativo. Escolha outro nas configurações.");
                OpenSettings();
            }
            else if (_args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            {
                OpenSettings();
            }
            else if (_args.Contains("--snippets", StringComparer.OrdinalIgnoreCase))
            {
                OpenSnippets();
            }
            else if (_args.Contains("--actions", StringComparer.OrdinalIgnoreCase))
            {
                OpenActions();
            }
            else if (!background || _args.Contains("--show", StringComparer.OrdinalIgnoreCase))
            {
                _launcher.ShowLauncher(ArgValue("--query"));
            }
        });
    }

    /// <summary>"--update-now": checks the update source and, if there is a newer version, installs it and restarts (for scripts and tests).</summary>
    private async Task UpdateNowAsync()
    {
        try
        {
            if (await _services!.Updates.CheckAsync() is not null) await _services.Updates.InstallAndRestartAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("--update-now failed", ex);
        }
    }

    /// <summary>
    /// Test aid: with APPARATUS_DEBUG_FREEZE_UI_MS=n the UI thread blocks for n ms, a few seconds after start-up, so the safety net
    /// (Windows' own Start menu opens when this program is frozen) can be exercised for real. Does nothing otherwise.
    /// </summary>
    private void FreezeUiForTesting()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("APPARATUS_DEBUG_FREEZE_UI_MS"), out int ms) || ms <= 0) return;
        var timer = _ui.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(4);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            AppLog.Warn($"[test aid] freezing the UI thread for {ms} ms");
            Thread.Sleep(ms);
        };
        timer.Start();
    }

    /// <summary>Value following a command-line switch ("--query chrome"), or null.</summary>
    private string? ArgValue(string name)
    {
        int i = Array.FindIndex(_args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < _args.Length ? _args[i + 1] : null;
    }

    private IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        string hotkey = _services?.ActiveHotkey?.ToString() ?? "";
        var items = new List<TrayMenuItem>
        {
            new TrayMenuItem(CmdOpen, hotkey.Length > 0 ? $"Abrir Apparatus\t{hotkey}" : "Abrir Apparatus"),
            new TrayMenuItem(CmdStart, "Menu Iniciar do Apparatus"),
            new TrayMenuItem(CmdActions, "Ações e atalhos…"),
            new TrayMenuItem(CmdSnippets, "Snippets…"),
            new TrayMenuItem(CmdSettings, "Configurações"),
            TrayMenuItem.Divider,
            new TrayMenuItem(CmdExpansion, "Expandir snippets ao digitar", Checked: _services?.Settings.Current.SnippetExpansion == true),
            new TrayMenuItem(CmdClipboard, "Guardar histórico da área de transferência", Checked: _services?.Settings.Current.ClipboardHistory == true),
            new TrayMenuItem(CmdStartKey, "Tecla Windows abre o menu do Apparatus", Checked: _services?.Settings.Current.StartMenuEnabled == true),
            new TrayMenuItem(CmdAutostart, "Iniciar com o Windows", Checked: _services?.Settings.Current.RunAtStartup == true),
            new TrayMenuItem(CmdReindex, "Reindexar arquivos"),
            TrayMenuItem.Divider,
            new TrayMenuItem(CmdQuit, "Sair"),
        };
        if (_services?.CaptureHost is { IsEnabled: true } capture)
        {
            string key = capture.ActiveHotkey?.ToString() ?? "";
            items.Insert(1, new TrayMenuItem(CmdCapture, key.Length > 0 ? $"Capturar a tela\t{key}" : "Capturar a tela"));
        }
        return items;
    }

    private void OnTrayCommand(int id)
    {
        switch (id)
        {
            case CmdOpen: _launcher?.ShowLauncher(); break;
            case CmdSettings: OpenSettings(); break;
            case CmdCapture: CaptureFromSettings(); break;
            case CmdStart: _launcher?.HideLauncher(); _services?.StartHost?.Show(); break;
            case CmdStartKey: _services?.ApplySettings(s => s.StartMenuEnabled = !s.StartMenuEnabled); break;
            case CmdSnippets: OpenSnippets(); break;
            case CmdActions: OpenActions(); break;
            case CmdClipboard: _services?.ApplySettings(s => s.ClipboardHistory = !s.ClipboardHistory); break;
            case CmdExpansion:
                _services?.ApplySettings(s => s.SnippetExpansion = !s.SnippetExpansion);
                break;
            case CmdAutostart:
                _services?.ApplySettings(s => s.RunAtStartup = !s.RunAtStartup);
                break;
            case CmdReindex: _services?.ReindexNow(); break;
            case CmdQuit: Quit(); break;
        }
    }

    /// <summary>The settings window (a side bar of groups plus a page); one at a time, created on demand and dropped when it is closed.</summary>
    private SettingsWindow EnsureSettingsWindow()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(_services!);
            _settings.Closed += (_, _) => _settings = null;
        }
        return _settings;
    }

    /// <summary>Opens the settings window on a page, as an ordinary window apart from the launcher.</summary>
    public void OpenSettings(string page = "general")
    {
        if (_services is null) return;
        _launcher?.HideLauncher();
        EnsureSettingsWindow().ShowPage(page);
    }

    /// <summary>Opens the snippets page of the settings window.</summary>
    public void OpenSnippets(bool startNew = false, Apparatus.Core.Snippets.Snippet? draft = null)
    {
        if (_services is null) return;
        _launcher?.HideLauncher();
        EnsureSettingsWindow().ShowSnippets(startNew, draft);
    }

    /// <summary>Opens the actions page of the settings window.</summary>
    public void OpenActions(bool startNew = false, Apparatus.Core.Actions.ActionDefinition? draft = null)
    {
        if (_services is null) return;
        _launcher?.HideLauncher();
        EnsureSettingsWindow().ShowActions(startNew, draft);
    }

    /// <summary>"Capturar agora" in the settings: minimizes the settings window, then starts the capture.</summary>
    public void CaptureFromSettings()
    {
        _launcher?.HideLauncher();
        _settings?.Minimize();
        // Give Windows a moment to repaint the screen without the launcher before it is read.
        var timer = _ui.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => _services?.CaptureHost?.StartCapture();
        timer.Start();
    }

    private void Quit()
    {
        AppLog.Info("Apparatus exiting");
        _launcher?.AllowClose();
        _services?.StartHost?.Close();
        _settings?.CloseNow();
        _launcher?.Close();
        _host?.Dispose();
        _services?.Dispose();
        _instance.Dispose();
        Exit();
    }
}
