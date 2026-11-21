using Apparatus.Core;
using Apparatus.Core.Actions;
using Apparatus.Core.Input;
using Apparatus.Native;
using Microsoft.UI.Dispatching;

namespace Apparatus.App;

public enum HotkeyState
{
    /// <summary>The action has no hotkey, or is disabled.</summary>
    None,
    /// <summary>Registered with Windows right now.</summary>
    Armed,
    /// <summary>Valid, but not active at the moment (another application is in front, or a more specific binding took the keys).</summary>
    Waiting,
    /// <summary>Windows refused it (usually another program owns the keys).</summary>
    Failed,
}

public sealed record HotkeyStatus(HotkeyState State, string Message);

/// <summary>
/// Owns the action runner and keeps the hotkeys of the actions registered with Windows. Because a registered
/// hotkey swallows its keys, an action limited to some applications is only registered while one of them has
/// the focus; the set is recomputed every time the foreground application changes.
/// </summary>
public sealed class ActionService : IDisposable
{
    private const int FirstHotkeyId = 0x1000;
    private const int LastHotkeyId = 0xBFFF;

    private readonly AppServices _services;
    private readonly DispatcherQueue _ui;
    private readonly ActionRunner _runner;
    private readonly Dictionary<HotkeySpec, (int Id, long ActionId)> _registered = new();
    private readonly Dictionary<HotkeySpec, string> _failed = new();
    private Dictionary<long, HotkeyStatus> _status = new();
    private string _foreground = "";
    private int _nextId = FirstHotkeyId;
    private int _suspended;

    public ActionService(AppServices services, DispatcherQueue ui)
    {
        _services = services;
        _ui = ui;
        _runner = new ActionRunner(
            new IStepExecutor[] { new WindowsStepExecutor(), new SnippetStepExecutor(services) },
            new ForegroundProbe());

        services.Actions.LauncherHotkey = services.Settings.Current.Hotkey;
        services.Host.HotkeyPressed += OnHotkeyPressed;
        services.Host.ForegroundChanged += process =>
        {
            _foreground = process;
            Rearm(retryFailed: false);
        };
        services.Actions.Changed += (_, _) => _ui.TryEnqueue(() => Rearm(retryFailed: true));

        _foreground = KeyTranslator.ForegroundProcessName();
        services.Host.StartForegroundTracking();
        Rearm(retryFailed: true);
    }

    /// <summary>Raised on the UI thread whenever the registration state of any hotkey may have changed.</summary>
    public event EventHandler? StatusChanged;

    public HotkeyStatus StatusOf(long actionId) =>
        _status.TryGetValue(actionId, out var s) ? s : new HotkeyStatus(HotkeyState.None, "");

    public bool IsRunning(long actionId) => _runner.IsRunning(actionId);

    // --------------------------------------------------------- registration

    /// <summary>Frees every action hotkey (used while the user records a new one, so it is not swallowed).</summary>
    public void SuspendHotkeys()
    {
        if (_suspended++ > 0) return;
        foreach (var reg in _registered.Values) _services.Host.UnregisterHotkey(reg.Id);
        _registered.Clear();
    }

    public void ResumeHotkeys()
    {
        if (_suspended == 0 || --_suspended > 0) return;
        Rearm(retryFailed: true);
    }

    private void Rearm(bool retryFailed)
    {
        if (_suspended > 0) return;
        var host = _services.Host;
        var bindings = _services.Actions.Bindings;
        var active = HotkeyPlan.ActiveFor(bindings, _foreground).ToDictionary(b => b.Spec);

        foreach (var (spec, reg) in _registered.ToList())
        {
            if (active.TryGetValue(spec, out var b) && b.ActionId == reg.ActionId) continue;
            host.UnregisterHotkey(reg.Id);
            _registered.Remove(spec);
        }

        if (retryFailed) _failed.Clear();
        foreach (var (spec, binding) in active)
        {
            if (_registered.ContainsKey(spec) || _failed.ContainsKey(spec)) continue;
            int id = _nextId++;
            if (_nextId > LastHotkeyId) _nextId = FirstHotkeyId;

            var result = host.RegisterHotkey(id, spec);
            if (result.Success) _registered[spec] = (id, binding.ActionId);
            else _failed[spec] = result.Win32Error == 1409
                ? $"{spec} já está registrado por outro aplicativo."
                : $"O Windows recusou {spec} (erro {result.Win32Error}).";
        }

        var status = new Dictionary<long, HotkeyStatus>();
        foreach (var b in bindings)
        {
            if (active.TryGetValue(b.Spec, out var winner) && winner.ActionId == b.ActionId)
            {
                status[b.ActionId] = _registered.ContainsKey(b.Spec)
                    ? new HotkeyStatus(HotkeyState.Armed, $"{b.Spec} está ativo.")
                    : new HotkeyStatus(HotkeyState.Failed, _failed.GetValueOrDefault(b.Spec, "Não foi possível registrar o atalho."));
            }
            else if (!b.AppliesTo(_foreground))
            {
                string where = b.Scope == ScopeMode.OnlyIn ? "vale só em " + string.Join(", ", b.Apps) : "não vale no aplicativo atual";
                status[b.ActionId] = new HotkeyStatus(HotkeyState.Waiting, $"{b.Spec} {where}.");
            }
            else
            {
                status[b.ActionId] = new HotkeyStatus(HotkeyState.Waiting, $"{b.Spec} está sendo usado por uma ação mais específica neste aplicativo.");
            }
        }
        _status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    // -------------------------------------------------------------- running

    private void OnHotkeyPressed(int id)
    {
        foreach (var reg in _registered.Values)
        {
            if (reg.Id == id) { Run(reg.ActionId); return; }
        }
    }

    /// <summary>Runs a saved action in the background. <paramref name="focusTarget"/> is brought to the front first (launcher runs).</summary>
    public void Run(long actionId, nint focusTarget = 0)
    {
        var action = _services.Actions.Get(actionId);
        if (action is null || !action.Enabled) return;
        _ = RunDefinitionAsync(action, focusTarget, record: true);
    }

    /// <summary>Runs a definition that may not be saved yet (the editor's "test" button).</summary>
    public async Task RunDefinitionAsync(ActionDefinition action, nint focusTarget = 0, bool record = false)
    {
        try
        {
            if (focusTarget != 0)
            {
                WindowHelpers.BringToForeground(focusTarget);
                for (int i = 0; i < 60 && KeyTranslator.ForegroundWindow() != focusTarget; i++) await Task.Delay(10).ConfigureAwait(false);
            }

            var result = await Task.Run(() => _runner.RunAsync(action)).ConfigureAwait(false);
            if (result is null)
            {
                AppLog.Info($"Action '{action.Name}' ignored: it is already running");
                return;
            }
            if (record && action.Id != 0) _services.Actions.RecordRun(action.Id);
            AppLog.Info($"Action '{action.Name}' finished in {result.Duration.TotalMilliseconds:0} ms (success: {result.Success})");

            if (!result.Success && !result.Cancelled)
            {
                _services.Host.ShowBalloon($"A ação “{action.Name}” falhou", result.FirstError ?? "Erro desconhecido.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Action '{action.Name}' crashed", ex);
            _services.Host.ShowBalloon($"A ação “{action.Name}” falhou", ex.Message);
        }
    }

    public void Dispose()
    {
        foreach (var reg in _registered.Values) _services.Host.UnregisterHotkey(reg.Id);
        _registered.Clear();
    }
}

/// <summary>The two steps that need Apparatus' own text insertion: typing text and inserting a snippet.</summary>
public sealed class SnippetStepExecutor : IStepExecutor
{
    private readonly AppServices _services;

    public SnippetStepExecutor(AppServices services) => _services = services;

    public bool CanExecute(StepKind kind) => kind is StepKind.TypeText or StepKind.InsertSnippet;

    public async Task ExecuteAsync(ActionStep step, CancellationToken ct)
    {
        var expander = _services.Expander ?? throw new InvalidOperationException("A inserção de texto ainda não está pronta.");
        if (step.Kind == StepKind.TypeText)
        {
            await expander.InsertTemplateAsync("Digitar texto", step.Get("text"), Core.Snippets.InsertMode.Auto).ConfigureAwait(false);
            return;
        }

        string name = step.Get("snippet").Trim();
        var snippet = _services.Snippets.All.FirstOrDefault(s => s.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))
                      ?? throw new InvalidOperationException($"O snippet “{name}” não existe mais.");
        await expander.InsertTemplateAsync(snippet.Name, snippet.Content, snippet.Mode).ConfigureAwait(false);
        _services.Snippets.RecordUse(snippet.Id);
    }
}
