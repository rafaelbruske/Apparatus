using Apparatus.Core.Settings;

namespace Apparatus.Core.Search.Providers;

/// <summary>
/// Rules for the list of web searches and quicklinks. Both are one kind of entry: a keyword, a name and a
/// target. A target with <c>{query}</c> is a search ("yt lofi" searches YouTube); without it, it is a plain
/// shortcut that opens a site, folder, file or program when picked.
/// </summary>
public static class Quicklinks
{
    public const string QueryPlaceholder = "{query}";

    public static bool IsSearch(WebSearchEngine e) => e.UrlTemplate.Contains(QueryPlaceholder, StringComparison.Ordinal);

    /// <summary>Null when the entry is acceptable, otherwise a message for the user.</summary>
    public static string? Validate(WebSearchEngine entry, IEnumerable<WebSearchEngine> others)
    {
        string keyword = entry.Keyword.Trim();
        if (entry.Name.Trim().Length == 0) return "Dê um nome.";
        if (keyword.Length == 0) return "Informe o atalho (por exemplo: gh).";
        if (keyword.Any(char.IsWhiteSpace)) return "O atalho não pode ter espaços.";
        if (keyword.Length > 16) return "O atalho pode ter no máximo 16 caracteres.";
        if (entry.UrlTemplate.Trim().Length == 0) return "Informe o destino: um endereço, uma pasta, um arquivo ou um programa.";
        if (others.Any(o => o.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase)))
            return $"O atalho “{keyword}” já existe.";
        return null;
    }
}
