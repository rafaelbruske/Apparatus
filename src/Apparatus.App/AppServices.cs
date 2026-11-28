using Apparatus.Core;
using Apparatus.Core.Actions;
using Apparatus.Core.Assistant;
using Apparatus.Core.Backup;
using Apparatus.Core.Clipboard;
using Apparatus.Core.Extensions;
using Apparatus.Core.Indexing;
using Apparatus.Core.Input;
using Apparatus.Core.Search;
using Apparatus.Core.Search.Providers;
using Apparatus.Core.Settings;
using Apparatus.Core.Snippets;
using Apparatus.Core.Start;
using Apparatus.Core.Storage;
using Apparatus.Native;

namespace Apparatus.App;

/// <summary>Composition root: owns the long-lived services and the operations that span several of them.</summary>
public sealed class AppServices : IDisposable
{
    private static readonly TimeSpan RescanInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan AppsRefreshInterval = TimeSpan.FromMinutes(15);

    private readonly ShellAppCatalog _catalog = new();
    private readonly Timer _rescanTimer;
    private DateTime _appsLoadedAt = DateTime.MinValue;
    private int _appsRefreshing;

    public AppServices(MessageHost host)
    {
        Host = host;
        Settings = new SettingsStore(AppPaths.SettingsFile);
        Usage = new UsageStore(AppPaths.DatabaseFile);
        Index = new FileIndex(AppPaths.IndexDirectory);
        Apps = new AppsProvider();
        Snippets = new SnippetStore(AppPaths.SnippetsFile);
        Snippets.SeedExamplesOnce();
        Actions = new ActionStore(AppPaths.ActionsFile);
        Actions.SeedExamplesOnce();
        ClipboardStore = CreateClipboardStore();
        AssistantKeys = AssistantKeyStore.CreateDefault(AppPaths.AssistantKeysFile);
        AssistantProviders = ProviderCatalog.LoadEmbedded();
        Assistant = new AssistantChat(CreateAssistantEngine, new AssistantSession(idleTimeout: AssistantIdleTimeout));
        Keyboard = new KeyboardHub();
        StartLayout = new StartLayoutStore(AppPaths.StartLayoutFile);
        Tweaks = new TweakService(new RegistryTweakStore(), AppPaths.TweakBackupFile);
        Backup = new BackupService(Settings, Snippets, Actions, StartLayout);
        Sync = new SyncService(this, Backup);
        Updates = new UpdateService(this);
        Extensions = new ExtensionHost(AppPaths.ExtensionsDirectory,
            () => Settings.Current.EnabledExtensions, () => Settings.Current.GlobalExtensions,
            (title, message) => host.ShowBalloon(title, message));
        Search = new SearchEngine(new ISearchProvider[]
        {
            Apps,
            new SnippetsProvider(Snippets),
            new ActionsProvider(Actions, target => AppSuggestions.NameOf(Apps.All, target)),
            new ClipboardProvider(ClipboardStore, () => Settings.Current),
            new ExtensionsProvider(Extensions),
            new FilesProvider(Index, () => Settings.Current),
            new WindowsSettingsProvider(),
            new CalculatorProvider(),
            new WebSearchProvider(() => Settings.Current),
            new InternalCommandsProvider(),
        }, Usage);

        _rescanTimer = new Timer(_ => ReindexIfStale(), null, RescanInterval, RescanInterval);
    }

    /// <summary>
    /// The history is encrypted with a key that Windows (DPAPI) ties to this user. If that key cannot be set up the history
    /// is switched off: it never falls back to storing text in clear.
    /// </summary>
    private static ClipboardHistoryStore CreateClipboardStore()
    {
        try
        {
            var cipher = ClipboardCipher.Open(AppPaths.ClipboardKeyFile, new DpapiKeyProtector());
            if (cipher.WasReset) AppLog.Warn("The clipboard encryption key was replaced; the previous history could not be read and was discarded");
            return new ClipboardHistoryStore(AppPaths.ClipboardFile, cipher);
        }
        catch (Exception ex)
        {
            AppLog.Error("Clipboard history is unavailable: its encryption key could not be set up", ex);
            return ClipboardHistoryStore.CreateUnavailable();
        }
    }

    /// <summary>The one low-level keyboard hook, shared by snippet expansion and the Win-key tap of the Start menu.</summary>
    public KeyboardHub Keyboard { get; }

    public StartLayoutStore StartLayout { get; }

    /// <summary>Taskbar and Windows-Start settings, with an exact undo.</summary>
    public TweakService Tweaks { get; }

    /// <summary>Export and import of everything the user configured.</summary>
    public BackupService Backup { get; }

    /// <summary>Keeps a backup in the sync folder, when one is chosen.</summary>
    public SyncService Sync { get; }

    /// <summary>Looks for and installs newer versions of this program.</summary>
    public UpdateService Updates { get; }

    /// <summary>The Start menu; set once the UI thread exists.</summary>
    public StartMenuService? StartHost { get; set; }

    public MessageHost Host { get; }
    public SettingsStore Settings { get; }
    public UsageStore Usage { get; }
    public FileIndex Index { get; }
    public AppsProvider Apps { get; }
    public SnippetStore Snippets { get; }
    public ActionStore Actions { get; }
    public ClipboardHistoryStore ClipboardStore { get; }

    /// <summary>API keys of the AI providers (DPAPI file). Keys are read only to build a client and never leave this store otherwise.</summary>
    public AssistantKeyStore AssistantKeys { get; }

    /// <summary>The providers the user can pick (assistant-providers.json).</summary>
    public ProviderCatalog AssistantProviders { get; }

    /// <summary>How long a conversation is kept in memory after the last message, when the user chose to keep it.</summary>
    public static readonly TimeSpan AssistantIdleTimeout = TimeSpan.FromMinutes(15);

    /// <summary>The conversation with the AI assistant (memory only).</summary>
    public AssistantChat Assistant { get; }

    private SettingsCatalog? _settingsCatalog;

    /// <summary>Null when the assistant is switched on and complete; otherwise what is missing, for the user.</summary>
    public string? AssistantMissing =>
        AssistantSetup.WhatIsMissing(AssistantSetup.Resolve(Settings.Current, AssistantProviders, AssistantKeys));

    /// <summary>"Provider · model", for the launcher footer (so it is always clear where a question goes).</summary>
    public string AssistantDescription()
    {
        var c = AssistantSetup.Resolve(Settings.Current, AssistantProviders, AssistantKeys);
        return c is null ? "" : c.Model.Length > 0 ? $"{c.Provider.Name} · {c.Model}" : c.Provider.Name;
    }

    /// <summary>Called when the launcher opens: forgets the conversation when it is off, expired, or the user wants a fresh one each time.</summary>
    public void OnLauncherShownForAssistant()
    {
        var s = Settings.Current;
        if (!s.AssistantEnabled || !s.AssistantKeepConversation) Assistant.Reset();
        else Assistant.ExpireIfIdle();
    }

    /// <summary>Builds an engine from the current settings. Throws <see cref="AssistantException"/>, worded for the user, when something is missing.</summary>
    private AssistantEngine CreateAssistantEngine()
    {
        var s = Settings.Current;
        if (!s.AssistantEnabled)
            throw new AssistantException(AssistantErrorKind.InvalidEndpoint, "O assistente está desligado. Ligue-o nas configurações.");

        var client = AssistantSetup.CreateClient(s, AssistantProviders, AssistantKeys);
        var catalog = _settingsCatalog ??= SettingsCatalog.LoadEmbedded();
        var version = typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "?";
        var prompt = new SystemPromptBuilder(catalog, version, Environment.OSVersion.Version.Build.ToString(), AppPaths.AssistantPromptFile);

        var shared = s.AssistantShareActionNames
            ? Actions.All.Where(a => a.Enabled).ToList()
            : new List<ActionDefinition>();
        var tools = new AssistantTools(catalog, new AssistantToolOptions
        {
            UserActions = shared,
            ValidateAction = Actions.Validate,
            ValidateSnippetTrigger = t => Snippets.ValidateTrigger(t),
        });

        // Tools only SUGGEST: each valid call becomes a button that does nothing until the user picks it.
        return new AssistantEngine(client, prompt, tools, Assistant.Session, () => shared.Select(a => a.Name).ToList());
    }
    public ExtensionHost Extensions { get; }

    /// <summary>Records and pastes clipboard history; set once the UI thread exists.</summary>
    public ClipboardService? ClipboardHost { get; set; }

    /// <summary>Carries out extension results; set once the UI thread exists.</summary>
    public ExtensionRunner? ExtensionRunner { get; set; }

    /// <summary>Shows the launcher with some text typed in (null for none). Set by the app once the launcher exists.</summary>
    public Action<string?>? ShowLauncher { get; set; }

    /// <summary>The screen capture (hotkey, selection windows); set once the UI thread exists.</summary>
    public CaptureService? CaptureHost { get; set; }

    /// <summary>Runs actions and keeps their hotkeys registered; set once the UI thread exists.</summary>
    public ActionService? ActionHost { get; set; }

    /// <summary>Set once the UI thread exists; expands triggers and inserts snippets.</summary>
    public SnippetService? Expander { get; set; }
    public SearchEngine Search { get; }

    /// <summary>The hotkey that is actually registered (it may differ from the setting if registration failed).</summary>
    public HotkeySpec? ActiveHotkey { get; private set; }

    // ------------------------------------------------------------ startup

    public HotkeyRegistration RegisterConfiguredHotkey()
    {
        if (!HotkeySpec.TryParse(Settings.Current.Hotkey, out var spec)) spec = HotkeySpec.Default;
        var result = Host.RegisterHotkey(spec);
        ActiveHotkey = result.Success ? spec : null;
        return result;
    }

    /// <summary>Loads the cached app list instantly, then refreshes it from the shell in the background.</summary>
    public void StartBackgroundWork()
    {
        var cached = Usage.LoadAppsCache();
        if (cached.Count > 0) Apps.SetApps(cached.Select(c => new AppEntry(c.Id, c.Name, c.ParsingName)));
        _ = RefreshAppsAsync(force: true);

        var settings = Settings.Current;
        if (settings.IndexFiles)
        {
            var roots = FileIndex.ResolveRoots(settings);
            Index.LoadSnapshots(roots);
            if (Index.NeedsRescan(roots, RescanInterval)) ReindexNow();
            else Index.WatchOnly(roots, settings);   // snapshots are fresh: just watch for changes until the next scan
        }
    }

    /// <summary>
    /// Icons are remembered together with history, favorites and pinned items. Steam-like apps used to be remembered with a
    /// generic icon; now that their real one is known, fix what was stored.
    /// </summary>
    private void CorrectRememberedAppIcons()
    {
        var icons = Apps.All.Where(a => a.IsUrlApp).ToDictionary(a => $"app:{a.Id}", a => a.IconKey, StringComparer.OrdinalIgnoreCase);
        if (icons.Count == 0) return;
        int n = Usage.UpdateIcons(icons) + StartLayout.UpdateIcons(icons);
        if (n > 0) AppLog.Info($"Corrected {n} remembered app icon(s)");
    }

    public async Task RefreshAppsAsync(bool force = false)
    {
        if (!force && DateTime.UtcNow - _appsLoadedAt < AppsRefreshInterval) return;
        if (Interlocked.Exchange(ref _appsRefreshing, 1) == 1) return;
        try
        {
            var apps = await _catalog.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            if (apps.Count == 0) return;
            Apps.SetApps(apps);
            CorrectRememberedAppIcons();
            Usage.SaveAppsCache(apps.Select(a => (a.Id, a.Name, a.ParsingName)));
            _appsLoadedAt = DateTime.UtcNow;
            AppLog.Info($"Apps refreshed: {apps.Count}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Refreshing the app list failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _appsRefreshing, 0);
        }
    }

    // ------------------------------------------------------------- index

    public void ReindexNow()
    {
        var settings = Settings.Current;
        if (!settings.IndexFiles) return;
        _ = Index.RebuildAsync(FileIndex.ResolveRoots(settings), settings);
    }

    private void ReindexIfStale()
    {
        var settings = Settings.Current;
        if (settings.IndexFiles && Index.NeedsRescan(FileIndex.ResolveRoots(settings), RescanInterval)) ReindexNow();
    }

    // ---------------------------------------------------------- settings

    /// <summary>Applies a settings change and reacts to what changed (autostart, indexing).</summary>
    public void ApplySettings(Action<AppSettings> mutate)
    {
        var old = Settings.Current;
        var next = old.Clone();
        mutate(next);
        Settings.Update(next);

        // Switching an extension off must also stop its process.
        foreach (var gone in old.EnabledExtensions.Except(next.EnabledExtensions, StringComparer.OrdinalIgnoreCase)) Extensions.Stop(gone);

        if (old.RunAtStartup != next.RunAtStartup)
        {
            try { AutoStart.Set(next.RunAtStartup); }
            catch (Exception ex) { AppLog.Error("Changing autostart failed", ex); }
        }

        bool indexingChanged = old.IndexFiles != next.IndexFiles
            || !(old.IndexRoots ?? new()).SequenceEqual(next.IndexRoots ?? new(), StringComparer.OrdinalIgnoreCase)
            || (old.IndexRoots is null) != (next.IndexRoots is null)
            || !old.ExcludedNames.SequenceEqual(next.ExcludedNames, StringComparer.OrdinalIgnoreCase)
            || old.MaxEntriesPerRoot != next.MaxEntriesPerRoot;
        if (indexingChanged)
        {
            if (next.IndexFiles) ReindexNow();
            else Index.CancelScan();
        }
    }

    // ----------------------------------------------------------- hotkey

    /// <summary>Tries a new hotkey; on failure the previous one is restored. Persists it on success.</summary>
    public HotkeyRegistration TryChangeHotkey(HotkeySpec spec)
    {
        var previous = ActiveHotkey;
        var result = Host.RegisterHotkey(spec);
        if (result.Success)
        {
            ActiveHotkey = spec;
            Actions.LauncherHotkey = spec.ToString();
            ApplySettings(s => s.Hotkey = spec.ToString());
        }
        else if (previous is { } p)
        {
            ActiveHotkey = Host.RegisterHotkey(p).Success ? p : null;
        }
        return result;
    }

    /// <summary>The action that already uses these keys, if any (Windows would report it as taken by another app).</summary>
    public string? FindActionUsing(HotkeySpec spec) =>
        Actions.All.FirstOrDefault(a => a.Enabled && HotkeySpec.TryParse(a.Hotkey, out var s) && s == spec)?.Name;

    /// <summary>Frees the hotkey temporarily (e.g. while the user is recording a new one).</summary>
    public void SuspendHotkey()
    {
        Host.UnregisterHotkey();
    }

    public void ResumeHotkey()
    {
        if (ActiveHotkey is { } spec) Host.RegisterHotkey(spec);
    }

    public void Dispose()
    {
        _rescanTimer.Dispose();
        StartHost?.Dispose();
        Sync.Dispose();
        ClipboardHost?.Dispose();
        CaptureHost?.Dispose();
        Extensions.Dispose();
        ClipboardStore.Dispose();
        ActionHost?.Dispose();
        Expander?.Dispose();
        Keyboard.Dispose();
        Actions.Dispose();
        Snippets.Dispose();
        Index.Dispose();
        Usage.Dispose();
    }
}
