using System.Text;
using System.Text.RegularExpressions;

namespace Apparatus.Core.Assistant;

/// <summary>
/// Builds the system prompt: the embedded assistant-system-prompt.md, optionally extended (or replaced) by a file the
/// user keeps in their config folder, with the {{placeholders}} filled in at run time.
/// </summary>
public sealed partial class SystemPromptBuilder
{
    /// <summary>First line of the user's file that makes it replace the built-in prompt instead of adding to it.</summary>
    public const string ReplaceMarker = "<!-- replace -->";

    public const int MaxOverrideChars = 20_000;
    public const int MaxActionNames = 50;
    public const int MaxActionNameChars = 60;

    private readonly string _template;
    private readonly SettingsCatalog _catalog;
    private readonly string _appVersion;
    private readonly string _windowsBuild;
    private readonly string? _overridePath;

    public SystemPromptBuilder(SettingsCatalog catalog, string appVersion, string windowsBuild, string? overridePath = null, string? template = null)
    {
        _catalog = catalog;
        _appVersion = appVersion;
        _windowsBuild = windowsBuild;
        _overridePath = overridePath;
        _template = template ?? LoadEmbeddedTemplate();
    }

    public static string LoadEmbeddedTemplate()
    {
        using var stream = typeof(SystemPromptBuilder).Assembly.GetManifestResourceStream("assistant-system-prompt.md")
            ?? throw new InvalidOperationException("assistant-system-prompt.md is not embedded");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Appended when no tools are offered, so the model does not promise buttons that will not appear.</summary>
    public const string NoToolsNote = """

        # Tools are not available in this conversation

        You cannot suggest actions or buttons now. Answer with text only. When a Windows settings page is relevant, name it and give the manual path (for example "Settings > Bluetooth & devices > Printers & scanners"). Do not say that you prepared or are offering a button.
        """;

    /// <param name="userActionNames">Names of the user's own actions, only when they agreed to share them; null or empty leaves them out.</param>
    /// <param name="toolsAvailable">False when the request goes out without tools (see <see cref="NoToolsNote"/>).</param>
    public string Build(IReadOnlyList<string>? userActionNames = null, bool toolsAvailable = true)
    {
        string text = _template;
        if (ReadOverride() is { } extra)
        {
            var lines = extra.Replace("\r\n", "\n").Split('\n').ToList();
            int first = lines.FindIndex(l => l.Trim().Length > 0);
            if (first >= 0 && lines[first].Trim() == ReplaceMarker)
            {
                lines.RemoveAt(first);
                text = string.Join('\n', lines).Trim();
            }
            else
            {
                text = text.TrimEnd() + "\n\n# Additional instructions from the user\n\n" + string.Join('\n', lines).Trim() + "\n";
            }
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app_version"] = _appVersion,
            ["windows_build"] = _windowsBuild,
            ["settings_catalog"] = _catalog.ToPromptLines(),
            ["user_actions"] = FormatActions(userActionNames),
        };
        // One pass: a value (an action name, say) is never scanned for placeholders itself.
        string prompt = Placeholder().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value).TrimEnd() + "\n";
        return toolsAvailable ? prompt : prompt + NoToolsNote.Replace("\r\n", "\n") + "\n";
    }

    private string? ReadOverride()
    {
        if (string.IsNullOrEmpty(_overridePath)) return null;
        try
        {
            if (!File.Exists(_overridePath)) return null;
            string content = File.ReadAllText(_overridePath, Encoding.UTF8);
            if (content.Length > MaxOverrideChars)
            {
                AppLog.Warn($"The assistant prompt file is larger than {MaxOverrideChars} characters; it was cut");
                content = content[..MaxOverrideChars];
            }
            return content.Trim().Length == 0 ? null : content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"The assistant prompt file could not be read ({ex.GetType().Name}); using the built-in one");
            return null;
        }
    }

    private static string FormatActions(IReadOnlyList<string>? names)
    {
        if (names is null || names.Count == 0) return "";
        var clean = names
            .Select(Clean)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxActionNames)
            .ToList();
        if (clean.Count == 0) return "";

        var sb = new StringBuilder();
        sb.Append("# The user's own actions (call `run_my_action` with one of these names, exactly)\n\n");
        foreach (var n in clean) sb.Append("- ").Append(n).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>Names are user text that ends up in the prompt: one line, no control characters, no placeholder syntax.</summary>
    internal static string Clean(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
            sb.Append(char.IsControl(c) ? ' ' : c);
        string flat = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Replace("{{", "{ {");
        return flat.Length <= MaxActionNameChars ? flat : flat[..MaxActionNameChars];
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex Placeholder();
}
