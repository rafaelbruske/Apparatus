using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Apparatus.Core.Snippets;

/// <summary>A value the user fills in when the snippet runs ({field:Nome}, {choice:Tom=formal|informal}).</summary>
public sealed record SnippetField(string Label, string Default, IReadOnlyList<string> Options)
{
    public bool IsChoice => Options.Count > 0;
}

public sealed record SnippetContext(DateTime Now, string? Clipboard, string UserName, string ComputerName, CultureInfo Culture)
{
    public static SnippetContext Create(string? clipboard) => new(
        DateTime.Now, clipboard, Environment.UserName, Environment.MachineName, CultureInfo.GetCultureInfo("pt-BR"));
}

/// <summary>
/// The text to insert plus where the caret should end up. <see cref="CursorFromEnd"/> is the number of
/// caret positions (grapheme clusters, "\r\n" counting once) to move left after inserting; 0 means "stay at the end".
/// Line breaks in <see cref="Text"/> are plain "\n".
/// </summary>
public sealed record RenderedSnippet(string Text, int CursorFromEnd);

/// <summary>
/// Snippet text with {variables}:
/// <list type="bullet">
/// <item><c>{date}</c>, <c>{time}</c>, <c>{datetime}</c> — optional offset and format: <c>{date+7d:dd/MM/yyyy}</c>
/// (units d, w, m, y, h, min).</item>
/// <item><c>{clipboard}</c>, <c>{cursor}</c>, <c>{uuid}</c>, <c>{user}</c>, <c>{computer}</c>.</item>
/// <item><c>{field:Label}</c> or <c>{field:Label=default}</c>; <c>{choice:Label=a|b|c}</c>. Repeating a label reuses its value.</item>
/// <item><c>{{</c> and <c>}}</c> produce literal braces. Unknown <c>{names}</c> stay as typed.</item>
/// </list>
/// </summary>
public sealed partial class SnippetTemplate
{
    private abstract record Part;
    private sealed record Literal(string Text) : Part;
    private sealed record Moment(string Kind, int Amount, string Unit, string? Format) : Part;
    private sealed record Simple(string Name) : Part;
    private sealed record Field(SnippetField Info) : Part;

    [GeneratedRegex(@"^(?<kind>date|time|datetime)(?<off>[+-]\d+(?:min|d|w|m|y|h))?(?::(?<fmt>.+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MomentPattern();

    [GeneratedRegex(@"^(?<amount>[+-]\d+)(?<unit>min|d|w|m|y|h)$", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetPattern();

    private static readonly HashSet<string> SimpleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "clipboard", "cursor", "uuid", "user", "computer",
    };

    private readonly List<Part> _parts;

    private SnippetTemplate(List<Part> parts, List<SnippetField> fields)
    {
        _parts = parts;
        Fields = fields;
    }

    /// <summary>Distinct fields in order of first appearance.</summary>
    public IReadOnlyList<SnippetField> Fields { get; }

    public bool UsesClipboard => _parts.Any(p => p is Simple { Name: var n } && n.Equals("clipboard", StringComparison.OrdinalIgnoreCase));

    public static SnippetTemplate Parse(string text)
    {
        var parts = new List<Part>();
        var fields = new List<SnippetField>();
        var literal = new StringBuilder();

        void FlushLiteral()
        {
            if (literal.Length == 0) return;
            parts.Add(new Literal(literal.ToString()));
            literal.Clear();
        }

        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{' && i + 1 < text.Length && text[i + 1] == '{') { literal.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < text.Length && text[i + 1] == '}') { literal.Append('}'); i++; continue; }
            if (c != '{') { literal.Append(c); continue; }

            int close = text.IndexOf('}', i + 1);
            if (close < 0) { literal.Append(c); continue; }   // unclosed brace: keep as text
            string inner = text[(i + 1)..close];

            var part = ParseVariable(inner, fields);
            if (part is null) { literal.Append('{').Append(inner).Append('}'); }
            else { FlushLiteral(); parts.Add(part); }
            i = close;
        }
        FlushLiteral();
        return new SnippetTemplate(parts, fields);
    }

    private static Part? ParseVariable(string inner, List<SnippetField> fields)
    {
        string trimmed = inner.Trim();
        if (SimpleNames.Contains(trimmed)) return new Simple(trimmed.ToLowerInvariant());

        var m = MomentPattern().Match(trimmed);
        if (m.Success)
        {
            int amount = 0;
            string unit = "d";
            if (m.Groups["off"].Success)
            {
                var o = OffsetPattern().Match(m.Groups["off"].Value);
                amount = int.Parse(o.Groups["amount"].Value, CultureInfo.InvariantCulture);
                unit = o.Groups["unit"].Value;
            }
            return new Moment(m.Groups["kind"].Value.ToLowerInvariant(), amount, unit,
                m.Groups["fmt"].Success ? m.Groups["fmt"].Value : null);
        }

        bool isField = trimmed.StartsWith("field:", StringComparison.OrdinalIgnoreCase);
        bool isChoice = trimmed.StartsWith("choice:", StringComparison.OrdinalIgnoreCase);
        if (isField || isChoice)
        {
            string spec = trimmed[(trimmed.IndexOf(':') + 1)..];
            int eq = spec.IndexOf('=');
            string label = (eq >= 0 ? spec[..eq] : spec).Trim();
            string value = eq >= 0 ? spec[(eq + 1)..] : "";
            if (label.Length == 0) return null;

            SnippetField info;
            if (isChoice)
            {
                var options = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (options.Length == 0) return null;
                info = new SnippetField(label, options[0], options);
            }
            else
            {
                info = new SnippetField(label, value, Array.Empty<string>());
            }

            // The first declaration of a label wins; later ones just reuse its value.
            var existing = fields.FirstOrDefault(f => f.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
            if (existing is null) fields.Add(info);
            return new Field(existing ?? info);
        }
        return null;
    }

    public RenderedSnippet Render(SnippetContext context, IReadOnlyDictionary<string, string>? fieldValues = null)
    {
        var sb = new StringBuilder();
        int cursorAt = -1;

        foreach (var part in _parts)
        {
            switch (part)
            {
                case Literal l:
                    sb.Append(l.Text);
                    break;
                case Moment m:
                    sb.Append(FormatMoment(m, context));
                    break;
                case Simple s:
                    switch (s.Name)
                    {
                        case "clipboard": sb.Append((context.Clipboard ?? "").Replace("\r\n", "\n").Replace('\r', '\n')); break;
                        case "cursor": if (cursorAt < 0) cursorAt = sb.Length; break;
                        case "uuid": sb.Append(Guid.NewGuid().ToString()); break;
                        case "user": sb.Append(context.UserName); break;
                        case "computer": sb.Append(context.ComputerName); break;
                    }
                    break;
                case Field f:
                    sb.Append(ValueFor(f.Info, fieldValues));
                    break;
            }
        }

        string text = sb.ToString();
        int fromEnd = cursorAt < 0 ? 0 : CountCaretSteps(text[cursorAt..]);
        return new RenderedSnippet(text, fromEnd);
    }

    private static string ValueFor(SnippetField field, IReadOnlyDictionary<string, string>? values)
    {
        if (values is not null)
        {
            foreach (var kv in values)
            {
                if (kv.Key.Equals(field.Label, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
        }
        return field.Default;
    }

    private static string FormatMoment(Moment m, SnippetContext ctx)
    {
        var t = ctx.Now;
        if (m.Amount != 0)
        {
            t = m.Unit switch
            {
                "d" => t.AddDays(m.Amount),
                "w" => t.AddDays(7 * m.Amount),
                "m" => t.AddMonths(m.Amount),
                "y" => t.AddYears(m.Amount),
                "h" => t.AddHours(m.Amount),
                "min" => t.AddMinutes(m.Amount),
                _ => t,
            };
        }
        string format = m.Format ?? m.Kind switch
        {
            "date" => "dd/MM/yyyy",
            "time" => "HH:mm",
            _ => "dd/MM/yyyy HH:mm",
        };
        try { return t.ToString(format, ctx.Culture); }
        catch (FormatException) { return "{" + m.Kind + "}"; }
    }

    /// <summary>Caret steps needed to move back over <paramref name="tail"/>: grapheme clusters, with "\n" counting once.</summary>
    public static int CountCaretSteps(string tail)
    {
        int steps = 0;
        var e = StringInfo.GetTextElementEnumerator(tail);
        while (e.MoveNext()) steps++;
        return steps;
    }
}
