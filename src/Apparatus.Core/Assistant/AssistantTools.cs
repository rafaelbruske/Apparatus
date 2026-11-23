using System.Text.Json;
using Apparatus.Core.Actions;
using Apparatus.Core.Snippets;

namespace Apparatus.Core.Assistant;

public enum SuggestionKind
{
    OpenWindowsSetting,
    OpenApparatusScreen,
    OpenLauncher,
    OpenUrl,
    RunMyAction,
    ProposeAction,
    ProposeSnippet,
}

public enum SuggestionRisk
{
    Low,
    /// <summary>Runs something the user (or the editor draft) defines; the card says so.</summary>
    Medium,
}

/// <summary>
/// A validated suggestion, ready to be shown as a button. Pure data: whoever runs it (the app) maps
/// <see cref="Kind"/> to code that already exists. <see cref="Payload"/> is the normalised value
/// (catalog URI, screen id, launcher text, URL, action name).
/// </summary>
public sealed record SuggestedAction(
    string CallId,
    SuggestionKind Kind,
    string Title,
    string Description,
    SuggestionRisk Risk,
    string Payload = "",
    long ActionId = 0,
    ActionDefinition? ActionDraft = null,
    Snippet? SnippetDraft = null);

/// <summary>Either a suggestion or the reason the model's call was thrown away.</summary>
public sealed record SuggestionResult(AssistantToolCall Call, SuggestedAction? Action, string? Rejection)
{
    public bool Accepted => Action is not null;
}

/// <summary>What the tools may look at. Everything optional: a missing piece disables the tool that needs it.</summary>
public sealed class AssistantToolOptions
{
    /// <summary>The user's own actions, only when they agreed to share their names. Empty hides <c>run_my_action</c>.</summary>
    public IReadOnlyList<ActionDefinition> UserActions { get; init; } = Array.Empty<ActionDefinition>();

    /// <summary>Same rules as <c>ActionStore.Validate</c> (name, steps, hotkey conflicts). Null skips that check.</summary>
    public Func<ActionDefinition, string?>? ValidateAction { get; init; }

    /// <summary>Same rules as <c>SnippetStore.ValidateTrigger</c>. Null skips that check.</summary>
    public Func<string, string?>? ValidateSnippetTrigger { get; init; }
}

/// <summary>
/// The tools offered to the model, and the checks that turn its calls into <see cref="SuggestedAction"/>s.
/// Whatever the model sends is untrusted text: every value is checked against a closed list or an existing
/// validator, and anything else is dropped. Nothing here runs anything.
/// </summary>
public sealed class AssistantTools
{
    public const string OpenWindowsSetting = "open_windows_setting";
    public const string OpenApparatusScreen = "open_apparatus_screen";
    public const string OpenLauncher = "open_launcher";
    public const string OpenUrl = "open_url";
    public const string RunMyAction = "run_my_action";
    public const string ProposeAction = "propose_action";
    public const string ProposeSnippet = "propose_snippet";

    /// <summary>The text the model sees as the "result" of a suggestion. It is not the result of running it.</summary>
    public const string SuggestedResult = "Suggested to the user; not executed yet.";

    public const int MaxArgumentChars = 20_000;
    public const int MaxSuggestionsPerAnswer = 3;
    public const int MaxStepsInProposedAction = 10;

    /// <summary>Screens of Apparatus itself the assistant may open. Quit, clear history and the like are not here, on purpose.</summary>
    public static readonly IReadOnlyList<(string Id, string Title)> Screens = new[]
    {
        ("settings", "Abrir as configurações do Apparatus"),
        ("snippets", "Abrir os snippets"),
        ("actions", "Abrir as ações"),
        ("extensions", "Abrir as extensões"),
    };

    /// <summary>Step kinds a proposed action may contain. No commands, no PowerShell, no typing text, no system commands.</summary>
    public static readonly IReadOnlySet<StepKind> AllowedStepKinds = new HashSet<StepKind>
    {
        StepKind.OpenApp, StepKind.OpenUrl, StepKind.OpenPath, StepKind.SendKeys, StepKind.Window,
        StepKind.Volume, StepKind.Media, StepKind.OpenLauncher, StepKind.Wait,
    };

    /// <summary>Programs that run arbitrary commands or scripts. An "open app" step may not point at them, or the ban on commands would mean nothing.</summary>
    private static readonly HashSet<string> BlockedPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "regedit", "reg", "msiexec",
        "bash", "wsl", "python", "pythonw", "node", "certutil", "bitsadmin", "schtasks", "sc", "net", "wmic", "msbuild",
    };

    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".scr", ".msi", ".reg", ".com", ".pif", ".cpl", ".jar",
    };

    // Flat optional text fields (instead of a free-form object): some providers reject an object without properties.
    private const string StepFields = """
        "target":{"type":"string","description":"OpenApp: program or file to open"},
        "args":{"type":"string","description":"OpenApp: arguments"},
        "url":{"type":"string","description":"OpenUrl: http(s) address"},
        "path":{"type":"string","description":"OpenPath: file or folder"},
        "keys":{"type":"string","description":"SendKeys: for example Ctrl+Shift+T"},
        "query":{"type":"string","description":"OpenLauncher: text to type in the launcher"},
        "ms":{"type":"string","description":"Wait: milliseconds"},
        "command":{"type":"string","description":"Window: maximize, minimize, restore, left-half, right-half, top-half, bottom-half, center, next-monitor"},
        "mode":{"type":"string","description":"Volume: up, down, mute or set"},
        "amount":{"type":"string","description":"Volume: percent, 0 to 100"},
        "key":{"type":"string","description":"Media: play-pause, next, previous or stop"}
        """;

    private readonly SettingsCatalog _settings;
    private readonly AssistantToolOptions _options;

    public AssistantTools(SettingsCatalog settings, AssistantToolOptions? options = null)
    {
        _settings = settings;
        _options = options ?? new AssistantToolOptions();
    }

    // ------------------------------------------------------------------------------------------ declarations

    /// <summary>The tools to offer the model. <c>run_my_action</c> only exists when there are actions the user agreed to share.</summary>
    public IReadOnlyList<AssistantToolSpec> Specs()
    {
        var list = new List<AssistantToolSpec>
        {
            new(OpenWindowsSetting,
                "Suggest opening a Windows settings page or system tool. The user must click to open it. Use only a uri from the catalog in the system prompt.",
                """{"type":"object","properties":{"uri":{"type":"string","description":"A uri copied exactly from the catalog, for example ms-settings:printers"}},"required":["uri"]}"""),
            new(OpenApparatusScreen,
                "Suggest opening one of Apparatus' own windows.",
                """{"type":"object","properties":{"screen":{"type":"string","enum":["settings","snippets","actions","extensions"]}},"required":["screen"]}"""),
            new(OpenLauncher,
                "Suggest opening the launcher with some text already typed, for example the name of an app or a keyword such as 'clip '.",
                """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}"""),
            new(OpenUrl,
                "Suggest opening a web page (http, https or mailto) in the default browser.",
                """{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}"""),
            new(ProposeAction,
                "Propose a new Apparatus action (a hotkey that runs one or more steps). It opens the action editor pre-filled; the user reviews and saves it. Nothing runs.",
                $$$"""
                {"type":"object","properties":{
                  "name":{"type":"string","description":"Short name"},
                  "hotkey":{"type":"string","description":"Optional, for example Ctrl+Alt+P"},
                  "steps":{"type":"array","items":{"type":"object","properties":{
                    "kind":{"type":"string","enum":["OpenApp","OpenUrl","OpenPath","SendKeys","Window","Volume","Media","OpenLauncher","Wait"]},
                    {{{StepFields}}}
                  },"required":["kind"]}}
                },"required":["name","steps"]}
                """),
            new(ProposeSnippet,
                "Propose a new text snippet. It opens the snippet editor pre-filled; the user reviews and saves it.",
                """{"type":"object","properties":{"name":{"type":"string"},"trigger":{"type":"string","description":"Optional text that expands while typing, for example ;email"},"text":{"type":"string"}},"required":["text"]}"""),
        };

        if (_options.UserActions.Any(a => a.Enabled))
        {
            list.Add(new(RunMyAction,
                "Suggest running one of the user's own actions, by its exact name from the list in the system prompt.",
                """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}"""));
        }
        return list;
    }

    // ------------------------------------------------------------------------------------------ validation

    /// <summary>Checks every call of one answer. At most <see cref="MaxSuggestionsPerAnswer"/> are accepted; the rest are rejected.</summary>
    public IReadOnlyList<SuggestionResult> Evaluate(IEnumerable<AssistantToolCall> calls)
    {
        var results = new List<SuggestionResult>();
        int accepted = 0;
        foreach (var call in calls)
        {
            var result = Evaluate(call);
            if (result.Accepted && ++accepted > MaxSuggestionsPerAnswer)
                result = new SuggestionResult(call, null, $"Too many suggestions in one answer (the limit is {MaxSuggestionsPerAnswer}).");
            results.Add(result);
        }
        return results;
    }

    public SuggestionResult Evaluate(AssistantToolCall call)
    {
        try
        {
            if (call.ArgumentsJson.Length is 0 or > MaxArgumentChars)
                return Reject(call, "The arguments are missing, unreadable or too long.");

            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            var args = doc.RootElement;
            if (args.ValueKind != JsonValueKind.Object) return Reject(call, "The arguments must be a JSON object.");

            return call.Name switch
            {
                OpenWindowsSetting => CheckSetting(call, args),
                OpenApparatusScreen => CheckScreen(call, args),
                OpenLauncher => CheckLauncher(call, args),
                OpenUrl => CheckUrl(call, args),
                RunMyAction => CheckRunMyAction(call, args),
                ProposeAction => CheckProposeAction(call, args),
                ProposeSnippet => CheckProposeSnippet(call, args),
                _ => Reject(call, $"Unknown tool '{call.Name}'."),
            };
        }
        catch (JsonException)
        {
            return Reject(call, "The arguments are not valid JSON.");
        }
    }

    private SuggestionResult CheckSetting(AssistantToolCall call, JsonElement args)
    {
        if (!TryString(args, "uri", out var uri)) return Reject(call, "Missing 'uri'.");
        var page = _settings.Find(uri);
        if (page is null) return Reject(call, $"'{Shorten(uri)}' is not in the settings catalog.");
        bool isSettings = page.Uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase);
        return Accept(call, new SuggestedAction(call.Id, SuggestionKind.OpenWindowsSetting, $"Abrir: {page.Name}",
            isSettings ? "Configurações do Windows" : "Ferramenta do Windows", SuggestionRisk.Low, page.Uri));
    }

    private SuggestionResult CheckScreen(AssistantToolCall call, JsonElement args)
    {
        if (!TryString(args, "screen", out var id)) return Reject(call, "Missing 'screen'.");
        var screen = Screens.FirstOrDefault(s => s.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
        if (screen.Id is null) return Reject(call, $"Unknown screen '{Shorten(id)}'. Allowed: {string.Join(", ", Screens.Select(s => s.Id))}.");
        return Accept(call, new SuggestedAction(call.Id, SuggestionKind.OpenApparatusScreen, screen.Title, "Apparatus", SuggestionRisk.Low, screen.Id));
    }

    private SuggestionResult CheckLauncher(AssistantToolCall call, JsonElement args)
    {
        if (!TryString(args, "query", out var query)) return Reject(call, "Missing 'query'.");
        query = query.Trim();
        if (query.Length == 0) return Reject(call, "'query' is empty.");
        if (query.Length > 200 || query.Any(char.IsControl)) return Reject(call, "'query' is too long or has control characters.");
        return Accept(call, new SuggestedAction(call.Id, SuggestionKind.OpenLauncher, $"Abrir o launcher com “{query}”", "Apparatus", SuggestionRisk.Low, query));
    }

    private SuggestionResult CheckUrl(AssistantToolCall call, JsonElement args)
    {
        if (!TryString(args, "url", out var url)) return Reject(call, "Missing 'url'.");
        url = url.Trim();
        if (url.Length > 2000 || url.Any(char.IsControl)) return Reject(call, "'url' is too long or has control characters.");
        if (StepCatalog.Validate(ActionStep.Create(StepKind.OpenUrl, ("url", url))) is not null)
            return Reject(call, "Only http://, https:// and mailto: addresses are accepted.");
        string host = Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Host : url;
        return Accept(call, new SuggestedAction(call.Id, SuggestionKind.OpenUrl, $"Abrir {Shorten(host, 60)}", "No navegador padrão", SuggestionRisk.Low, url));
    }

    private SuggestionResult CheckRunMyAction(AssistantToolCall call, JsonElement args)
    {
        if (!TryString(args, "name", out var name)) return Reject(call, "Missing 'name'.");
        var matches = _options.UserActions.Where(a => a.Enabled && a.Name.Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return Reject(call, $"There is no enabled action named '{Shorten(name)}'.");
        if (matches.Count > 1) return Reject(call, $"More than one action is named '{Shorten(name)}'.");
        var action = matches[0];
        return Accept(call, new SuggestedAction(call.Id, SuggestionKind.RunMyAction, $"Executar: {action.Name}", action.Describe(), SuggestionRisk.Medium, action.Name, action.Id));
    }

    private SuggestionResult CheckProposeAction(AssistantToolCall call, JsonElement args)
    {
        if (!TryString(args, "name", out var name) || name.Trim().Length == 0) return Reject(call, "Missing 'name'.");
        name = name.Trim();
        if (name.Length > 60 || name.Any(char.IsControl)) return Reject(call, "'name' is too long or has control characters.");
        if (!args.TryGetProperty("steps", out var stepsElement) || stepsElement.ValueKind != JsonValueKind.Array) return Reject(call, "Missing 'steps'.");
        if (stepsElement.GetArrayLength() is 0 or > MaxStepsInProposedAction)
            return Reject(call, $"An action needs between 1 and {MaxStepsInProposedAction} steps.");

        var steps = new List<ActionStep>();
        int index = 0;
        foreach (var element in stepsElement.EnumerateArray())
        {
            index++;
            var step = ReadStep(element, out var problem);
            if (step is null) return Reject(call, $"Step {index}: {problem}");
            steps.Add(step);
        }

        var action = new ActionDefinition { Name = name, Steps = steps };
        string hotkey = TryString(args, "hotkey", out var h) ? h.Trim() : "";
        if (hotkey.Length > 0)
        {
            action.Hotkey = hotkey;
            // The hotkey is only a hint: if it is invalid or taken, drop it and let the user choose in the editor.
            if (_options.ValidateAction?.Invoke(action) is not null) action.Hotkey = "";
        }
        if (_options.ValidateAction?.Invoke(action) is { } invalid) return Reject(call, invalid);

        return Accept(call, new SuggestedAction(call.Id, SuggestionKind.ProposeAction, $"Criar a ação “{name}”",
            action.Describe() + (action.Hotkey.Length > 0 ? $"  ·  atalho {action.Hotkey}" : ""), SuggestionRisk.Medium, name, ActionDraft: action));
    }

    private static ActionStep? ReadStep(JsonElement element, out string problem)
    {
        problem = "";
        if (element.ValueKind != JsonValueKind.Object) { problem = "must be an object."; return null; }
        if (!TryString(element, "kind", out var kindText) || !Enum.TryParse<StepKind>(kindText.Trim(), ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
        {
            problem = "unknown 'kind'.";
            return null;
        }
        if (!AllowedStepKinds.Contains(kind))
        {
            problem = $"'{kind}' steps are not allowed in a proposed action.";
            return null;
        }

        // Copy only the fields this kind defines; anything else the model added is ignored, never stored.
        var step = ActionStep.Create(kind);
        foreach (var field in StepCatalog.Get(kind).Fields)
        {
            if (field.Type == FieldType.Bool) continue;   // 'admin' and friends are never set by the model
            if (!TryString(element, field.Key, out var value)) continue;
            value = value.Trim();
            if (value.Length > 500 || value.Any(char.IsControl)) { problem = $"'{field.Key}' is too long or has control characters."; return null; }
            step.Args[field.Key] = value;
        }
        foreach (var field in StepCatalog.Get(kind).Fields.Where(f => f.Default.Length > 0))
            step.Args.TryAdd(field.Key, field.Default);

        if (kind == StepKind.OpenApp && BlockedTarget(step.Get("target")) is { } why) { problem = why; return null; }
        if (kind == StepKind.OpenPath && BlockedExtensions.Contains(Path.GetExtension(step.Get("path").TrimEnd('\\', '/'))))
        {
            problem = "scripts and installers cannot be opened by a proposed action.";
            return null;
        }
        if (StepCatalog.Validate(step) is { } invalid) { problem = invalid; return null; }
        return step;
    }

    private static string? BlockedTarget(string target)
    {
        string file = target.Trim().Trim('"');
        string name = Path.GetFileNameWithoutExtension(file);
        if (BlockedPrograms.Contains(name) || BlockedExtensions.Contains(Path.GetExtension(file)))
            return $"'{Shorten(target, 40)}' runs commands or scripts and cannot be used in a proposed action.";
        return null;
    }

    private SuggestionResult CheckProposeSnippet(AssistantToolCall call, JsonElement args)
    {
        if (!TryString(args, "text", out var text) || text.Trim().Length == 0) return Reject(call, "Missing 'text'.");
        if (text.Length > 4000) return Reject(call, "'text' is too long.");

        string trigger = TryString(args, "trigger", out var t) ? t.Trim() : "";
        string note = "";
        if (trigger.Length > 0 && _options.ValidateSnippetTrigger?.Invoke(trigger) is not null)
        {
            // Taken or malformed: keep the snippet, without a trigger, and let the user pick one in the editor.
            trigger = "";
            note = "  ·  o gatilho sugerido não pôde ser usado";
        }

        string name = TryString(args, "name", out var n) && n.Trim().Length > 0 ? n.Trim() : (trigger.Length > 0 ? trigger : Shorten(text.Trim().Replace('\n', ' '), 30));
        if (name.Length > 60 || name.Any(char.IsControl)) name = Shorten(name.Replace('\n', ' '), 60);

        var snippet = new Snippet { Name = name, Trigger = trigger, Content = text, AutoExpand = trigger.Length > 0 };
        return Accept(call, new SuggestedAction(call.Id, SuggestionKind.ProposeSnippet, $"Criar o snippet “{name}”",
            snippet.Preview(70) + note, SuggestionRisk.Low, name, SnippetDraft: snippet));
    }

    // ------------------------------------------------------------------------------------------ helpers

    private static SuggestionResult Accept(AssistantToolCall call, SuggestedAction action) => new(call, action, null);

    private SuggestionResult Reject(AssistantToolCall call, string reason) => new(call, null, reason);

    /// <summary>Strings, numbers and booleans count as text (models often send "5" as 5). Null, arrays and objects do not.</summary>
    private static bool TryString(JsonElement obj, string name, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var p)) return false;
        switch (p.ValueKind)
        {
            case JsonValueKind.String: value = p.GetString() ?? ""; return true;
            case JsonValueKind.Number: value = p.GetRawText(); return true;
            case JsonValueKind.True: value = "true"; return true;
            case JsonValueKind.False: value = "false"; return true;
            default: return false;
        }
    }

    private static string Shorten(string text, int max = 40) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
