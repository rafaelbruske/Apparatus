namespace Apparatus.Core.Snippets;

/// <summary>How the text reaches the target app.</summary>
public enum InsertMode
{
    /// <summary>Type short single-line text; paste anything longer or multi-line (so Enter never "sends" a chat message).</summary>
    Auto = 0,
    /// <summary>Always type it as keystrokes (never touches the clipboard).</summary>
    Type = 1,
    /// <summary>Always paste it through the clipboard (restoring the previous text clipboard afterwards).</summary>
    Paste = 2,
}

public sealed class Snippet
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Text that expands in place while typing (e.g. ";email"). Empty means launcher-only.</summary>
    public string Trigger { get; set; } = "";

    /// <summary>Template text: plain text plus {variables}, see <see cref="SnippetTemplate"/>.</summary>
    public string Content { get; set; } = "";

    public string Category { get; set; } = "";

    public List<string> Tags { get; set; } = new();

    public bool Favorite { get; set; }

    /// <summary>Whether typing the trigger expands it automatically (needs a non-empty trigger).</summary>
    public bool AutoExpand { get; set; } = true;

    public InsertMode Mode { get; set; } = InsertMode.Auto;

    public int UseCount { get; set; }

    public DateTime? LastUsedUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public Snippet Clone()
    {
        var copy = (Snippet)MemberwiseClone();
        copy.Tags = new List<string>(Tags);
        return copy;
    }

    /// <summary>One-line preview of the content for lists.</summary>
    public string Preview(int max = 80)
    {
        var line = Content.Replace("\r", "").Replace('\n', ' ').Trim();
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }
}

public sealed class SnippetValidationException : Exception
{
    public SnippetValidationException(string message) : base(message) { }
}
