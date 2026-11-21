using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Apparatus.Core.Extensions;

public sealed class ExtensionManifestException : Exception
{
    public ExtensionManifestException(string message) : base(message) { }
}

/// <summary>
/// <c>extension.json</c>: what to run and when to ask it. An extension is any program that speaks the
/// line-based JSON protocol below on stdin/stdout; PowerShell, Python, Node, a compiled tool...
/// </summary>
public sealed partial class ExtensionManifest
{
    public const string FileName = "extension.json";

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{1,63}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,16}$")]
    private static partial Regex KeywordPattern();

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";

    /// <summary>Typing "keyword text" in the launcher sends "text" to the extension. Optional.</summary>
    public string Keyword { get; set; } = "";

    public string Command { get; set; } = "";

    /// <summary>Arguments; a relative path that exists inside the extension folder is made absolute, and {dir} is replaced by the folder.</summary>
    public List<string> Args { get; set; } = new();

    /// <summary>"persistent" keeps one process running and talks to it; "oneshot" starts a process per request.</summary>
    public string Mode { get; set; } = "persistent";

    public int TimeoutMs { get; set; } = 2000;

    /// <summary>Wait this long after the last keystroke before asking (default: none for persistent, 250 for oneshot).</summary>
    public int? DebounceMs { get; set; }

    [JsonIgnore] public string Folder { get; set; } = "";

    [JsonIgnore] public bool IsPersistent => !Mode.Equals("oneshot", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore] public int EffectiveDebounceMs => DebounceMs ?? (IsPersistent ? 0 : 250);

    public static ExtensionManifest Load(string folder)
    {
        string path = Path.Combine(folder, FileName);
        ExtensionManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ExtensionManifest>(File.ReadAllText(path), ExtensionJson.Options)
                       ?? throw new ExtensionManifestException("O arquivo está vazio.");
        }
        catch (JsonException ex) { throw new ExtensionManifestException($"JSON inválido: {ex.Message}"); }
        catch (IOException ex) { throw new ExtensionManifestException($"Não foi possível ler: {ex.Message}"); }

        manifest.Folder = Path.GetFullPath(folder);
        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        if (!IdPattern().IsMatch(Id)) throw new ExtensionManifestException("“id” deve ter 2 a 64 caracteres: letras minúsculas, números, ponto, hífen ou sublinhado.");
        if (Name.Trim().Length == 0) throw new ExtensionManifestException("Falta o “name”.");
        if (Command.Trim().Length == 0) throw new ExtensionManifestException("Falta o “command”.");
        if (Keyword.Length > 0 && !KeywordPattern().IsMatch(Keyword)) throw new ExtensionManifestException("“keyword” deve ter até 16 letras, números, ponto, hífen ou sublinhado, sem espaços.");
        if (!Mode.Equals("persistent", StringComparison.OrdinalIgnoreCase) && !Mode.Equals("oneshot", StringComparison.OrdinalIgnoreCase))
            throw new ExtensionManifestException("“mode” deve ser “persistent” ou “oneshot”.");
        if (TimeoutMs is < 200 or > 30000) throw new ExtensionManifestException("“timeoutMs” deve ficar entre 200 e 30000.");
        if (DebounceMs is < 0 or > 5000) throw new ExtensionManifestException("“debounceMs” deve ficar entre 0 e 5000.");
    }

    /// <summary>The program to start, resolved against the extension folder when it is a file there.</summary>
    public string ResolveCommand() => ResolvePath(Command.Replace("{dir}", Folder));

    public IReadOnlyList<string> ResolveArgs() => Args.Select(a => ResolvePath(a.Replace("{dir}", Folder))).ToList();

    private string ResolvePath(string value)
    {
        if (value.Length == 0 || Path.IsPathRooted(value) || value.StartsWith('-')) return value;
        string candidate = Path.Combine(Folder, value);
        return File.Exists(candidate) ? candidate : value;
    }
}

internal static class ExtensionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

/// <summary>What happens when a result is chosen. Types: open, copy, insert, reveal, callback, none.</summary>
public sealed class ExtAction
{
    public string Type { get; set; } = "callback";
    public string? Target { get; set; }
    public string? Text { get; set; }
    public string? Path { get; set; }
    public JsonElement? Data { get; set; }
}

public sealed class ExtResult
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }

    /// <summary>"path:C:\file.exe" or "shell:…" for a real icon; anything else is ignored.</summary>
    public string? Icon { get; set; }

    public double? Score { get; set; }

    /// <summary>Enter. Defaults to a callback to the extension.</summary>
    public ExtAction? Action { get; set; }

    /// <summary>Ctrl+Enter.</summary>
    public ExtAction? Alt { get; set; }
}

/// <summary>One line of the protocol, in either direction.</summary>
public sealed class ExtMessage
{
    public string Type { get; set; } = "";
    public int? Id { get; set; }
    public string? Query { get; set; }
    public string? Keyword { get; set; }
    public string? Result { get; set; }
    public JsonElement? Data { get; set; }
    public List<ExtResult>? Results { get; set; }
    public string? Title { get; set; }
    public string? Message { get; set; }

    public string ToLine() => JsonSerializer.Serialize(this, ExtensionJson.Options);

    public static ExtMessage? Parse(string line)
    {
        line = line.Trim().TrimStart('\uFEFF');
        if (line.Length == 0 || line[0] != '{') return null;
        try { return JsonSerializer.Deserialize<ExtMessage>(line, ExtensionJson.Options); }
        catch (JsonException) { return null; }
    }
}

/// <summary>What the launcher stores in a result so it can act on it later.</summary>
public sealed class ExtPayload
{
    public string E { get; set; } = "";
    public string R { get; set; } = "";
    public ExtAction? A { get; set; }
    public ExtAction? Alt { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, ExtensionJson.Options);

    public static ExtPayload? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<ExtPayload>(json, ExtensionJson.Options); }
        catch (JsonException) { return null; }
    }
}
