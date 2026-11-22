using Apparatus.Core;
using Apparatus.Core.Start;
using Apparatus.Native;
using Microsoft.UI.Dispatching;

namespace Apparatus.App;

/// <summary>
/// Owns the Start menu window and what opens it: a tap on the Windows key and, optionally, a click on the Start button of the
/// taskbar. Both interceptors listen only while the feature is on, and both step aside (so Windows' own menu opens) whenever
/// this program cannot be trusted to answer: its UI thread is frozen, or the failsafe has switched the replacement off.
/// </summary>
public sealed class StartMenuService : IDisposable
{
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan OfficialWatchWindow = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan OfficialWatchEvery = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan MenuShouldHaveAppearedWithin = TimeSpan.FromMilliseconds(1500);

    private readonly AppServices _services;
    private readonly DispatcherQueue _ui;
    private readonly StartMenuWindow _window;
    private readonly UiHeartbeat _heartbeat = new();
    private readonly DispatcherQueueTimer _beat;
    private readonly WinKeyInterceptor _winKey;
    private readonly StartClickInterceptor _click;

    public StartMenuService(AppServices services, DispatcherQueue ui, ResultExecutor executor)
    {
        _services = services;
        _ui = ui;
        _window = new StartMenuWindow(services, executor);
        Failsafe = new StartFailsafe(AppPaths.StartFailsafeFile, OsBuild.Current());

        _winKey = new WinKeyInterceptor(services.Keyboard, CanIntercept);
        _winKey.Tapped += () => OnTriggered("tecla Windows");
        _click = new StartClickInterceptor(CanIntercept);
        _click.Clicked += () => OnTriggered("botão Iniciar");

        _beat = ui.CreateTimer();
        _beat.Interval = HeartbeatEvery;
        _beat.Tick += (_, _) => _heartbeat.Beat();
        _beat.Start();

        services.Settings.Changed += (_, _) => _ui.TryEnqueue(ApplySettings);
        ApplySettings();
        AppLog.Info($"Start replacement: Windows {Failsafe.CurrentBuild}, verified build '{Failsafe.VerifiedBuild}', probation={Failsafe.InProbation}");
    }

    public StartFailsafe Failsafe { get; }

    public bool WinKeyActive => _winKey.IsActive;

    public bool ClickActive => _click.IsActive;

    /// <summary>The Start buttons the click interceptor currently knows (empty when it cannot find one).</summary>
    public IReadOnlyList<ScreenRect> ButtonRects => _click.ButtonRects;

    public bool IsVisible => _window.IsMenuVisible;

    /// <summary>Raised on the UI thread when the failsafe has given the Start menu back to Windows.</summary>
    public event Action<string>? FailsafeTripped;

    // ----------------------------------------------------------- interception

    /// <summary>Asked by the hooks on every tap and click: false means "let Windows have it".</summary>
    private bool CanIntercept() =>
        _heartbeat.IsAlive() && !Failsafe.IsTripped && !StartInterceptPolicy.StepAside(ScreenState.Current());

    public void ApplySettings()
    {
        var s = _services.Settings.Current;

        // Switching it on again after a trip is the user saying "try again".
        if (s.StartMenuEnabled && Failsafe.IsTripped) Failsafe.Reset();

        _winKey.SetActive(s.StartMenuEnabled);
        _click.SetActive(s.StartMenuEnabled && s.StartButtonClick);
    }

    /// <summary>Runs on the hook thread: be quick. Notes whether Windows' menu was already up, then hands over to the UI thread.</summary>
    private void OnTriggered(string source)
    {
        bool officialWasOpen = OfficialStart.IsOpen();
        int before = _window.ActionCount;
        bool opening = !_window.IsMenuVisible;

        // Watch for Windows' own menu from now on, not after the UI has answered: it opens the moment the key is released and
        // is dismissed again as soon as Apparatus' window takes the focus, so a late look would never see it.
        if (opening && !officialWasOpen)
        {
            _ = Task.Run(() =>
            {
                if (OfficialStart.OpensWithin(OfficialWatchWindow, OfficialWatchEvery))
                {
                    AppLog.Warn($"Windows' own Start menu opened together with Apparatus' ({source})");
                    Verdict(Failsafe.RecordProblem(StartProblem.OfficialAlsoOpened));
                }
                else
                {
                    Failsafe.RecordCleanUse();
                }
            });
        }

        if (!_ui.TryEnqueue(() => _window.ToggleMenu()))
        {
            _ = Task.Run(() => Rescue("a interface não respondeu"));
            return;
        }

        // Dead-man's switch: whatever goes wrong on the UI side, the request must have been carried out shortly.
        _ = Task.Run(async () =>
        {
            await Task.Delay(MenuShouldHaveAppearedWithin).ConfigureAwait(false);
            if (_window.ActionCount == before) Rescue("o menu não apareceu a tempo");
        });
    }

    /// <summary>The request was lost (or the UI never answered): open Windows' menu so the user is never left without one.</summary>
    private void Rescue(string why)
    {
        AppLog.Warn($"Apparatus' Start menu could not open ({why}); opening Windows' menu instead");
        OfficialStart.Open();
        Verdict(Failsafe.RecordProblem(StartProblem.MenuDidNotAppear));
    }

    private void Verdict(FailsafeVerdict verdict)
    {
        if (!verdict.Trip) return;
        AppLog.Warn("Start failsafe tripped: " + verdict.Message);
        _ui.TryEnqueue(() =>
        {
            _services.ApplySettings(s => s.StartMenuEnabled = false);   // also stops both interceptors
            _window.HideMenu();
            _services.Host.ShowBalloon("Voltei ao menu Iniciar do Windows",
                $"Desliguei o menu do Apparatus porque {verdict.Message} Você pode tentar de novo em Configurações → Menu Iniciar do Apparatus.");
            FailsafeTripped?.Invoke(verdict.Message ?? "");
        });
    }

    // ------------------------------------------------------------------- api

    public void Prewarm(Action? done = null) => _window.Prewarm(done);

    public void Show() => _window.ShowMenu();

    public void ShowWithQuery(string query) => _window.ShowWithQuery(query);

    public void ShowAllApps() => _window.ShowAllApps();

    public void Toggle() => _window.ToggleMenu();

    public void Hide() => _window.HideMenu();

    public void Close()
    {
        _window.AllowClose();
        _window.Close();
    }

    public void Dispose()
    {
        _beat.Stop();
        _winKey.Dispose();
        _click.Dispose();
    }
}
