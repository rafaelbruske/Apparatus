using Apparatus.Core;
using Apparatus.Core.Capture;
using Apparatus.Core.Input;
using Apparatus.Core.Settings;
using Apparatus.Core.Start;
using Apparatus.Native;
using Microsoft.UI.Dispatching;

namespace Apparatus.App;

/// <summary>What the user needs to know after switching the capture on or changing a hotkey.</summary>
public sealed record CaptureOutcome(bool Success, string Message);

/// <summary>The two capture hotkeys: the selection window, or the automatic full-screen capture that saves straight into the folder.</summary>
public enum CaptureKind { Normal, Automatic }

/// <summary>
/// The screen capture feature: owns its hotkeys, the Windows option it has to switch off (Print Screen opening the Snipping Tool) and the
/// capture sessions. Switching it on turns that Windows option off and remembers its value; switching it off puts the value back.
/// </summary>
public sealed class CaptureService : IDisposable
{
    /// <summary>Ids of the capture hotkeys in the message window (the launcher uses 0xA990, actions 0x1000–0xBFFF).</summary>
    public const int HotkeyId = 0xA991, QuickHotkeyId = 0xA992;

    private readonly AppServices _services;
    private readonly DispatcherQueue _ui;
    private readonly IFrameSource _source = new GdiFrameSource();
    private CaptureSession? _session;

    public CaptureService(AppServices services, DispatcherQueue ui)
    {
        _services = services;
        _ui = ui;
        services.Host.HotkeyPressed += id =>
        {
            if (id == HotkeyId) _ui.TryEnqueue(StartCapture);
            else if (id == QuickHotkeyId) _ui.TryEnqueue(CaptureScreenNow);
        };
    }

    public bool IsEnabled => _services.Settings.Current.CaptureEnabled;

    /// <summary>The hotkeys that are registered right now, or null.</summary>
    public HotkeySpec? ActiveHotkey { get; private set; }
    public HotkeySpec? ActiveQuickHotkey { get; private set; }

    /// <summary>True while the Windows option "Print Screen opens the Snipping Tool" is switched on.</summary>
    public bool WindowsSnippingOnPrintScreen => _services.Tweaks.Current(TweakCatalog.PrintScreenSnippingId) != 0;

    /// <summary>The folder "save straight away" writes to.</summary>
    public string SaveFolder => CaptureNaming.SaveFolder(_services.Settings.Current.CaptureSaveFolder,
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));

    private static int IdOf(CaptureKind kind) => kind == CaptureKind.Normal ? HotkeyId : QuickHotkeyId;

    private static HotkeySpec HotkeyOf(AppSettings s, CaptureKind kind) => kind == CaptureKind.Normal
        ? HotkeySpec.TryParse(s.CaptureHotkey, out var main) ? main : new HotkeySpec(HotkeyModifiers.None, HotkeySpec.PrintScreenKey)
        : HotkeySpec.TryParse(s.CaptureQuickHotkey, out var quick) ? quick : new HotkeySpec(HotkeyModifiers.Shift, HotkeySpec.PrintScreenKey);

    /// <summary>At start-up: registers the hotkeys and makes sure Windows has not switched its own option back on.</summary>
    public CaptureOutcome Restore()
    {
        if (!IsEnabled) return new CaptureOutcome(true, "");
        return Arm();
    }

    public CaptureOutcome Enable()
    {
        var outcome = Arm();
        if (outcome.Success) _services.ApplySettings(s => s.CaptureEnabled = true);
        else _services.Tweaks.Restore(TweakCatalog.PrintScreenSnippingId);   // nothing was switched on, so Windows keeps what it had
        return outcome;
    }

    public void Disable()
    {
        _services.Host.UnregisterHotkey(HotkeyId);
        _services.Host.UnregisterHotkey(QuickHotkeyId);
        ActiveHotkey = ActiveQuickHotkey = null;
        _services.ApplySettings(s => s.CaptureEnabled = false);
        var result = _services.Tweaks.Restore(TweakCatalog.PrintScreenSnippingId);
        if (!result.Success) AppLog.Warn("Could not give Print Screen back to the Windows Snipping Tool: " + result.Message);
    }

    /// <summary>Tries a new hotkey; on failure the previous one stays.</summary>
    public CaptureOutcome ChangeHotkey(HotkeySpec spec, CaptureKind kind)
    {
        if (_services.FindActionUsing(spec) is { } action)
            return new CaptureOutcome(false, $"{spec} já é o atalho da ação “{action}”. Escolha outra combinação.");
        if (spec == _services.ActiveHotkey)
            return new CaptureOutcome(false, $"{spec} já abre o Apparatus. Escolha outra combinação.");
        var other = HotkeyOf(_services.Settings.Current, kind == CaptureKind.Normal ? CaptureKind.Automatic : CaptureKind.Normal);
        if (spec == other)
            return new CaptureOutcome(false, $"{spec} já é o outro atalho da captura. Escolha outra combinação.");

        var previous = kind == CaptureKind.Normal ? ActiveHotkey : ActiveQuickHotkey;
        if (IsEnabled)
        {
            var result = _services.Host.RegisterHotkey(IdOf(kind), spec);
            if (!result.Success)
            {
                if (previous is { } p) _services.Host.RegisterHotkey(IdOf(kind), p);
                return new CaptureOutcome(false, FailureMessage(spec, result.Win32Error));
            }
            if (kind == CaptureKind.Normal) ActiveHotkey = spec; else ActiveQuickHotkey = spec;
        }
        _services.ApplySettings(s => { if (kind == CaptureKind.Normal) s.CaptureHotkey = spec.ToString(); else s.CaptureQuickHotkey = spec.ToString(); });
        return new CaptureOutcome(true, $"Atalho alterado para {spec}.");
    }

    /// <summary>Frees the hotkeys while the user records a new one in the settings.</summary>
    public void Suspend()
    {
        _services.Host.UnregisterHotkey(HotkeyId);
        _services.Host.UnregisterHotkey(QuickHotkeyId);
    }

    public void Resume()
    {
        if (!IsEnabled) return;
        if (ActiveHotkey is { } main) _services.Host.RegisterHotkey(HotkeyId, main);
        if (ActiveQuickHotkey is { } quick) _services.Host.RegisterHotkey(QuickHotkeyId, quick);
    }

    private CaptureOutcome Arm()
    {
        var settings = _services.Settings.Current;
        var main = HotkeyOf(settings, CaptureKind.Normal);
        var quick = HotkeyOf(settings, CaptureKind.Automatic);

        // Windows would also react to Print Screen (it opens its own tool), so its option goes off first.
        bool switchedOff = false;
        if (WindowsSnippingOnPrintScreen)
        {
            var result = _services.Tweaks.Apply(TweakCatalog.PrintScreenSnippingId, 0);
            switchedOff = result.Success;
            if (!result.Success)
                AppLog.Warn("Could not switch off the Windows option that opens the Snipping Tool on Print Screen: " + result.Message);
        }

        var registration = _services.Host.RegisterHotkey(HotkeyId, main);
        // Windows lets go of Print Screen a moment after its option is switched off.
        for (int attempt = 0; switchedOff && !registration.Success && registration.Win32Error == 1409 && attempt < 4; attempt++)
        {
            Thread.Sleep(250);
            registration = _services.Host.RegisterHotkey(HotkeyId, main);
        }
        ActiveHotkey = registration.Success ? main : null;
        if (!registration.Success) return new CaptureOutcome(false, FailureMessage(main, registration.Win32Error));
        AppLog.Info($"Screen capture hotkey registered: {main}");

        string warning = "";
        var quickRegistration = quick == main ? new HotkeyRegistration(false, 1409) : _services.Host.RegisterHotkey(QuickHotkeyId, quick);
        ActiveQuickHotkey = quickRegistration.Success ? quick : null;
        if (quickRegistration.Success) AppLog.Info($"Screen capture quick-save hotkey registered: {quick}");
        else warning = "O atalho de salvar direto não ficou ativo: " + FailureMessage(quick, quickRegistration.Win32Error);

        if (WindowsSnippingOnPrintScreen && main.VirtualKey == HotkeySpec.PrintScreenKey)
            warning = "Ativado, mas o Windows não aceitou desligar a opção dele: Print Screen pode abrir as duas ferramentas. Desligue em Configurações → Acessibilidade → Teclado. " + warning;
        return new CaptureOutcome(true, warning.Trim());
    }

    /// <summary>Error 1409 means another program owns the keys; when a known capture program is running, it is named.</summary>
    private static string FailureMessage(HotkeySpec spec, int error)
    {
        if (error != 1409) return $"Não foi possível registrar {spec} (erro {error}).";
        string who = "";
        try
        {
            var names = KnownCaptureTools.FindRunning(System.Diagnostics.Process.GetProcesses().Select(p => p.ProcessName));
            if (names.Count > 0) who = $" Está em execução: {string.Join(", ", names)}. Feche-o (ou tire a tecla dele) e tente de novo, ou escolha outro atalho.";
        }
        catch (Exception ex)
        {
            AppLog.Warn("Listing the running programs failed: " + ex.Message);
        }
        return $"{spec} já está em uso por outro aplicativo.{(who.Length > 0 ? who : " Escolha outro atalho.")}";
    }

    // ------------------------------------------------------------ capturing

    /// <summary>Freezes every monitor and opens the selection windows. Ignored while one capture is already open.</summary>
    public void StartCapture()
    {
        if (_session is not null) return;
        AppLog.Info("Screen capture started");
        try
        {
            _session = CaptureSession.Start(_services, _source, () => _session = null);
        }
        catch (Exception ex)
        {
            _session = null;
            AppLog.Error("Starting the screen capture failed", ex);
            _services.Host.ShowBalloon("Captura de tela", "Não foi possível ler a tela: " + ex.Message);
        }
    }

    private bool _autoBusy;

    private const int FlashTicks = 12, FlashTickMs = 16;
    private const byte FlashStartAlpha = 150;

    /// <summary>A white flash that fades out in about 200 ms over the given areas. Fire and forget; a failure only means no flash.</summary>
    private void Flash(IReadOnlyList<RectD> areas)
    {
        try
        {
            var windows = areas.Select(a => new FlashWindow(a, FlashStartAlpha)).ToList();
            int tick = 0;
            var timer = _ui.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(FlashTickMs);
            timer.Tick += (_, _) =>
            {
                tick++;
                if (tick >= FlashTicks)
                {
                    timer.Stop();
                    foreach (var w in windows) w.Dispose();
                    return;
                }
                double left = 1 - (double)tick / FlashTicks;
                foreach (var w in windows) w.SetAlpha((byte)(FlashStartAlpha * left * left));   // quick at first, then a soft tail
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The capture flash could not be shown: " + ex.Message);
        }
    }

    /// <summary>The monitor of the window that has the focus; when there is none, the one under the mouse.</summary>
    private static MonitorInfoData FocusedMonitor(IReadOnlyList<MonitorInfoData> monitors)
    {
        var (x, y) = GdiFrameSource.ForegroundWindowCenter() ?? WindowHelpers.CursorPosition();
        return monitors.FirstOrDefault(m => m.Bounds.Contains(x, y)) ?? monitors[0];
    }

    /// <summary>
    /// The automatic capture: saves the focused monitor (or all of them, by setting) straight into the folder, with no selection window and no question.
    /// Says nothing unless the user turned on the notification (or something went wrong).
    /// </summary>
    public async void CaptureScreenNow()
    {
        if (_session is not null || _autoBusy) return;
        _autoBusy = true;
        try
        {
            var monitors = _source.Monitors();
            if (monitors.Count == 0) throw new InvalidOperationException("Nenhum monitor encontrado.");
            bool all = _services.Settings.Current.CaptureAutoAllMonitors;
            var focused = FocusedMonitor(monitors);
            CaptureTarget target = all
                ? new RegionTarget(RectD.Union(monitors.Select(m => m.Bounds)))
                : new MonitorTarget(focused.Index);
            var frame = _source.Grab(target);

            // After the screen was read, so the flash is never in the picture.
            if (_services.Settings.Current.CaptureAutoFlash) Flash((all ? monitors : new[] { focused }).Select(m => m.Bounds).ToList());

            var settings = _services.Settings.Current;
            var format = CaptureImageFormats.Parse(settings.CaptureFormat);
            var bytes = await CaptureImage.EncodeAsync(frame.Bgra, frame.Width, frame.Height, format, settings.CaptureQuality);
            string folder = SaveFolder;
            Directory.CreateDirectory(folder);
            string path = CaptureNaming.UniquePath(folder, CaptureNaming.FileName(DateTime.Now, CaptureImageFormats.Extension(format)), File.Exists);
            await File.WriteAllBytesAsync(path, bytes);
            AppLog.Info($"Automatic capture saved: {path}");
            if (_services.Settings.Current.CaptureNotifyOnSave) _services.Host.ShowBalloon("Captura salva", path);
        }
        catch (Exception ex)
        {
            AppLog.Error("The automatic capture failed", ex);
            _services.Host.ShowBalloon("Captura de tela", "Não foi possível salvar a captura: " + ex.Message);
        }
        finally
        {
            _autoBusy = false;
        }
    }

    public void Dispose()
    {
        _session?.Close();
        _services.Host.UnregisterHotkey(HotkeyId);
        _services.Host.UnregisterHotkey(QuickHotkeyId);
    }
}

/// <summary>One capture in progress: the frozen picture of each monitor, each under its own selection window.</summary>
public sealed class CaptureSession
{
    private readonly List<CaptureOverlayWindow> _windows = new();
    private readonly Action _onClosed;
    private bool _closed;

    private CaptureSession(AppServices services, Action onClosed)
    {
        Services = services;
        _onClosed = onClosed;
    }

    public AppServices Services { get; }

    public static CaptureSession Start(AppServices services, IFrameSource source, Action onClosed)
    {
        var session = new CaptureSession(services, onClosed);
        var monitors = source.Monitors();
        if (monitors.Count == 0) throw new InvalidOperationException("Nenhum monitor encontrado.");

        // Grab everything first, so the windows that appear next are not in the pictures.
        var frames = monitors.Select(m => (Monitor: m, Frame: source.Grab(new MonitorTarget(m.Index)))).ToList();

        var (cx, cy) = WindowHelpers.CursorPosition();
        var under = monitors.FirstOrDefault(m => m.Bounds.Contains(cx, cy)) ?? monitors[0];
        string color = services.Settings.Current.CaptureColor;
        foreach (var (monitor, frame) in frames)
        {
            var window = new CaptureOverlayWindow(session, monitor, frame, color);
            session._windows.Add(window);
            window.ShowOverMonitor(activate: monitor.Index == under.Index);
        }
        session._windows.First(w => w.Monitor.Index == under.Index).FocusMe();
        return session;
    }

    /// <summary>Only one monitor can hold a selection: the others drop theirs.</summary>
    public void SelectionStartedOn(CaptureOverlayWindow owner)
    {
        foreach (var w in _windows.Where(w => !ReferenceEquals(w, owner))) w.ClearSelection();
    }

    public void RememberColor(string hex) => Services.ApplySettings(s => s.CaptureColor = hex);

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        foreach (var w in _windows)
        {
            try { w.Close(); }
            catch (Exception ex) { AppLog.Warn("Closing a capture window failed: " + ex.Message); }
        }
        _windows.Clear();
        _onClosed();
    }
}
