using System.Diagnostics;

namespace Apparatus.Core.Actions;

public sealed record ForegroundInfo(string ProcessName, string WindowTitle);

/// <summary>Tells the runner which window has the focus right now (used by step conditions).</summary>
public interface IForegroundProbe
{
    ForegroundInfo Get();
}

/// <summary>Carries out one kind of step. Executors are platform specific; the runner is not.</summary>
public interface IStepExecutor
{
    bool CanExecute(StepKind kind);

    Task ExecuteAsync(ActionStep step, CancellationToken ct);
}

public enum StepOutcome { Done, Skipped, Failed }

public sealed record StepResult(int Index, ActionStep Step, StepOutcome Outcome, string? Error = null);

public sealed record ActionRunResult(bool Success, bool Cancelled, IReadOnlyList<StepResult> Steps, TimeSpan Duration)
{
    public string? FirstError => Steps.FirstOrDefault(s => s.Outcome == StepOutcome.Failed)?.Error;
}

/// <summary>
/// Runs an action: its steps in order, skipping those whose condition is false, waiting where asked and
/// following the action's error policy. Each action runs at most once at a time.
/// </summary>
public sealed class ActionRunner
{
    private readonly IReadOnlyList<IStepExecutor> _executors;
    private readonly IForegroundProbe _foreground;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly HashSet<long> _running = new();

    public ActionRunner(IEnumerable<IStepExecutor> executors, IForegroundProbe foreground,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _executors = executors.ToList();
        _foreground = foreground;
        _delay = delay ?? Task.Delay;
    }

    public bool IsRunning(long actionId)
    {
        lock (_running) return _running.Contains(actionId);
    }

    /// <summary>Runs the action. If the same action is already running, returns null without doing anything.</summary>
    public async Task<ActionRunResult?> RunAsync(ActionDefinition action, CancellationToken ct = default)
    {
        lock (_running)
        {
            if (!_running.Add(action.Id)) return null;
        }

        var clock = Stopwatch.StartNew();
        var results = new List<StepResult>();
        bool cancelled = false, success = true;
        try
        {
            for (int i = 0; i < action.Steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = action.Steps[i];

                if (!ConditionHolds(step.When))
                {
                    results.Add(new StepResult(i, step, StepOutcome.Skipped));
                    continue;
                }

                try
                {
                    if (step.Kind == StepKind.Wait)
                    {
                        await _delay(TimeSpan.FromMilliseconds(Math.Clamp(step.GetInt("ms"), 0, 600_000)), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var executor = _executors.FirstOrDefault(e => e.CanExecute(step.Kind))
                                       ?? throw new NotSupportedException($"Nenhum executor para o passo {step.Kind}.");
                        await executor.ExecuteAsync(step, ct).ConfigureAwait(false);
                    }
                    results.Add(new StepResult(i, step, StepOutcome.Done));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Action '{action.Name}' step {i + 1} ({step.Kind}) failed: {ex.Message}");
                    results.Add(new StepResult(i, step, StepOutcome.Failed, ex.Message));
                    success = false;
                    if (action.OnError == ErrorPolicy.Stop) break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            success = false;
        }
        finally
        {
            lock (_running) _running.Remove(action.Id);
        }
        return new ActionRunResult(success, cancelled, results, clock.Elapsed);
    }

    private bool ConditionHolds(StepCondition? condition)
    {
        if (condition is not { IsSet: true }) return true;
        var fg = _foreground.Get();
        string value = condition.Value.Trim();
        return condition.Kind switch
        {
            ConditionKind.ActiveProcessIs => HotkeyBinding.NormalizeProcess(fg.ProcessName).Equals(HotkeyBinding.NormalizeProcess(value), StringComparison.OrdinalIgnoreCase),
            ConditionKind.ActiveProcessIsNot => !HotkeyBinding.NormalizeProcess(fg.ProcessName).Equals(HotkeyBinding.NormalizeProcess(value), StringComparison.OrdinalIgnoreCase),
            ConditionKind.WindowTitleContains => fg.WindowTitle.Contains(value, StringComparison.OrdinalIgnoreCase),
            ConditionKind.WindowTitleNotContains => !fg.WindowTitle.Contains(value, StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }
}
