using System.Text.Json.Serialization;

namespace Apparatus.Core.Actions;

/// <summary>What a single step of an action does. Stored by name, so never rename a value.</summary>
public enum StepKind
{
    OpenApp,
    OpenUrl,
    OpenPath,
    RunCommand,
    RunPowerShell,
    SendKeys,
    TypeText,
    InsertSnippet,
    Wait,
    Window,
    Volume,
    Brightness,
    Media,
    System,
    OpenLauncher,
}

public enum ScopeMode
{
    /// <summary>The hotkey works in every application.</summary>
    Everywhere,
    /// <summary>Only while one of the listed applications has the focus.</summary>
    OnlyIn,
    /// <summary>Everywhere except in the listed applications.</summary>
    ExceptIn,
}

public enum ErrorPolicy
{
    /// <summary>A failing step ends the action.</summary>
    Stop,
    /// <summary>Carry on with the next step.</summary>
    Continue,
}

public enum ConditionKind
{
    None,
    ActiveProcessIs,
    ActiveProcessIsNot,
    WindowTitleContains,
    WindowTitleNotContains,
}

/// <summary>Run a step only when the focused window matches. Evaluated right before the step, so earlier steps can change it.</summary>
public sealed class StepCondition
{
    public ConditionKind Kind { get; set; } = ConditionKind.None;

    public string Value { get; set; } = "";

    public bool IsSet => Kind != ConditionKind.None && Value.Length > 0;
}

public sealed class ActionStep
{
    public StepKind Kind { get; set; }

    /// <summary>Settings of the step; the keys are defined by <see cref="StepCatalog"/>.</summary>
    public Dictionary<string, string> Args { get; set; } = new();

    public StepCondition? When { get; set; }

    [JsonIgnore]
    public bool HasCondition => When is { IsSet: true };

    public string Get(string key, string fallback = "") => Args.TryGetValue(key, out var v) ? v : fallback;

    public bool GetBool(string key, bool fallback = false) =>
        Args.TryGetValue(key, out var v) ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : fallback;

    public int GetInt(string key, int fallback = 0) =>
        Args.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;

    public ActionStep Clone() => new()
    {
        Kind = Kind,
        Args = new Dictionary<string, string>(Args),
        When = When is null ? null : new StepCondition { Kind = When.Kind, Value = When.Value },
    };

    public static ActionStep Create(StepKind kind, params (string Key, string Value)[] args) =>
        new() { Kind = kind, Args = args.ToDictionary(a => a.Key, a => a.Value) };
}

/// <summary>
/// Something the user can trigger: from a hotkey, from the launcher or from another action.
/// A plain action has one step; a macro has several, run in order.
/// </summary>
public sealed class ActionDefinition
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    public string Category { get; set; } = "";

    /// <summary>Global hotkey in text form ("Ctrl+Alt+P"), or empty for launcher-only.</summary>
    public string Hotkey { get; set; } = "";

    public ScopeMode Scope { get; set; } = ScopeMode.Everywhere;

    /// <summary>Process names (without .exe) the <see cref="Scope"/> refers to.</summary>
    public List<string> ScopeApps { get; set; } = new();

    public bool Enabled { get; set; } = true;

    public ErrorPolicy OnError { get; set; } = ErrorPolicy.Stop;

    public List<ActionStep> Steps { get; set; } = new();

    public int RunCount { get; set; }

    public DateTime? LastRunUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool IsMacro => Steps.Count > 1;

    public ActionDefinition Clone()
    {
        var copy = (ActionDefinition)MemberwiseClone();
        copy.ScopeApps = new List<string>(ScopeApps);
        copy.Steps = Steps.Select(s => s.Clone()).ToList();
        return copy;
    }

    /// <summary>One line describing what the action does, for lists and the launcher.</summary>
    /// <param name="appName">Passed to <see cref="StepCatalog.Summarize"/>.</param>
    public string Describe(int max = 90, Func<string, string?>? appName = null)
    {
        string text = Steps.Count switch
        {
            0 => "(sem passos)",
            1 => StepCatalog.Summarize(Steps[0], appName),
            _ => $"Macro de {Steps.Count} passos: " + string.Join(" → ", Steps.Take(3).Select(s => StepCatalog.Summarize(s, appName))) + (Steps.Count > 3 ? " …" : ""),
        };
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}

public sealed class ActionValidationException : Exception
{
    public ActionValidationException(string message) : base(message) { }
}
