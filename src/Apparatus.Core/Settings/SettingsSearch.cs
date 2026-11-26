using System.Globalization;
using System.Text;

namespace Apparatus.Core.Settings;

/// <summary>
/// The matching rule of the search box of the settings window: case and accents do not matter and every word typed has to appear
/// in the text. The window applies it to the blocks of each page (a heading and its card) to decide what stays on the screen.
/// </summary>
public static class SettingsSearch
{
    /// <summary>Lower case without accents: "Área de transferência" becomes "area de transferencia".</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>The words of a query, normalized. Empty for a blank query.</summary>
    public static IReadOnlyList<string> Words(string? query) =>
        string.IsNullOrWhiteSpace(query) ? Array.Empty<string>() : Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>True when every word appears in <paramref name="text"/> (not normalized yet). No words never match.</summary>
    public static bool Matches(string text, IReadOnlyList<string> words)
    {
        if (words.Count == 0) return false;
        string normalized = Normalize(text);
        return words.All(w => normalized.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>
    /// Where the words of a query appear in <paramref name="text"/>, as (start, length) of the original text, in order and without overlaps.
    /// The comparison ignores case and accents, but the ranges point into the text as it is written, so the window can mark them.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> FindRanges(string text, IReadOnlyList<string> words)
    {
        if (words.Count == 0 || text.Length == 0) return Array.Empty<(int, int)>();

        // Normalize character by character, remembering which original character each normalized one came from.
        var normalized = new StringBuilder(text.Length);
        var origin = new List<int>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            string piece = char.IsSurrogate(text[i]) ? text[i].ToString() : Normalize(text[i].ToString());
            foreach (char c in piece)
            {
                normalized.Append(c);
                origin.Add(i);
            }
        }

        string haystack = normalized.ToString();
        var found = new List<(int Start, int End)>();
        foreach (string word in words)
        {
            if (word.Length == 0) continue;
            for (int at = haystack.IndexOf(word, StringComparison.Ordinal); at >= 0; at = haystack.IndexOf(word, at + word.Length, StringComparison.Ordinal))
                found.Add((origin[at], origin[at + word.Length - 1] + 1));
        }

        var merged = new List<(int Start, int Length)>();
        foreach (var (start, end) in found.OrderBy(f => f.Start).ThenBy(f => f.End))
        {
            if (merged.Count > 0 && start <= merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, Math.Max(last.Start + last.Length, end) - last.Start);
            }
            else
            {
                merged.Add((start, end - start));
            }
        }
        return merged;
    }
}
