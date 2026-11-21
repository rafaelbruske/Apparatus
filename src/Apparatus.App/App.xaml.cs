using Apparatus.Core;
using Apparatus.Core.Input;
using Apparatus.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Apparatus.App;

public partial class App : Application
{
    private const int CmdOpen = 1, CmdSettings = 2, CmdAutostart = 3, CmdReindex = 4, CmdQuit = 5, CmdSnippets = 6, CmdExpansion = 7, CmdActions = 8, CmdClipboard = 9, CmdStart = 10, CmdStartKey = 11;

    private readonly SingleInstance _instance;
    private readonly string[] _args;
    private DispatcherQueue _ui = null!;
    private MessageHost? _host;
    private AppServices? _services;
    private LauncherWindow? _launcher;
    private SettingsWindow? _settings;
    private SnippetsWindow? _snippetsWindow;
    private ActionsWindow? _actionsWindow;

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
        var executor = new ResultExecutor(_services, OpenSettings, Quit, newSnippet => OpenSnippets(newSnippet), newAction => OpenActions(newAction));
        _launcher = new LauncherWindow(_services, executor);
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
        _host.HotkeyPressed += id => { if (id == MessageHost.LauncherHotkeyId) _launcher.ToggleLauncher(); };
        _host.ShowTrayIcon(AppPaths.AssetPath("apparatus.ico"), "Apparatus");

        var registration = _services.RegisterConfiguredHotkey();
        _services.StartBackgroundWork();
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

    /// <summary>Value following a command-line switch ("--query chrome"), or null.</summary>
    private string? ArgValue(string name)
    {
        int i = Array.FindIndex(_args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < _args.Length ? _args[i + 1] : null;
    }

    private IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        string hotkey = _services?.ActiveHotkey?.ToString() ?? "";
        return new[]
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
    }

    private void OnTrayCommand(int id)
    {
        switch (id)
        {
            case CmdOpen: _launcher?.ShowLauncher(); break;
            case CmdSettings: OpenSettings(); break;
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

    public void OpenSettings()
    {
        if (_services is null) return;
        _launcher?.HideLauncher();
        if (_settings is null)
        {
            _settings = new SettingsWindow(_services);
            _settings.Closed += (_, _) => _settings = null;
        }
        _settings.Activate();
        WindowHelpers.BringToForeground(WinRT.Interop.WindowNative.GetWindowHandle(_settings));
    }

    public void OpenSnippets(bool startNew = false)
    {
        if (_services is null) return;
        _launcher?.HideLauncher();
        if (_snippetsWindow is null)
        {
            _snippetsWindow = new SnippetsWindow(_services);
            _snippetsWindow.Closed += (_, _) => _snippetsWindow = null;
        }
        _snippetsWindow.Activate();
        WindowHelpers.BringToForeground(WinRT.Interop.WindowNative.GetWindowHandle(_snippetsWindow));
        if (startNew && _snippetsWindow is not null) _snippetsWindow.StartNewSnippet();
    }

    public void OpenActions(bool startNew = false)
    {
        if (_services is null) return;
        _launcher?.HideLauncher();
        if (_actionsWindow is null)
        {
            _actionsWindow = new ActionsWindow(_services);
            _actionsWindow.Closed += (_, _) => _actionsWindow = null;
        }
        _actionsWindow.Activate();
        WindowHelpers.BringToForeground(WinRT.Interop.WindowNative.GetWindowHandle(_actionsWindow));
        if (startNew) _actionsWindow.StartNewAction();
    }

    private void Quit()
    {
        AppLog.Info("Apparatus exiting");
        _launcher?.AllowClose();
        _services?.StartHost?.Close();
        _settings?.Close();
        _snippetsWindow?.Close();
        _actionsWindow?.Close();
        _launcher?.Close();
        _host?.Dispose();
        _services?.Dispose();
        _instance.Dispose();
        Exit();
    }
}
