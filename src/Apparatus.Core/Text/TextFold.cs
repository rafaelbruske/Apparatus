using System.Text;

namespace Apparatus.Core.Text;

/// <summary>
/// Case- and accent-insensitive character folding ("Configuração" matches "configuracao").
/// Allocation-free so it can run over millions of file names per keystroke.
/// </summary>
public static class TextFold
{
    private const int TableSize = 0x250; // Basic Latin through Latin Extended-B
    private static readonly char[] Table = BuildTable();

    private static char[] BuildTable()
    {
        var table = new char[TableSize];
        for (int i = 0; i < TableSize; i++)
        {
            char c = (char)i;
            char folded = char.ToLowerInvariant(c);
            if (i >= 0xC0)
            {
                // Strip diacritics: take the base letter of the canonical decomposition when it is ASCII.
                string d = folded.ToString().Normalize(NormalizationForm.FormD);
                if (d.Length > 0 && d[0] < 0x80) folded = d[0];
            }
            table[i] = folded;
        }
        return table;
    }

    public static char Fold(char c) => c < TableSize ? Table[c] : char.ToLowerInvariant(c);

    public static string Fold(string text)
    {
        if (text.Length == 0) return text;
        return string.Create(text.Length, text, static (span, src) =>
        {
            for (int i = 0; i < src.Length; i++) span[i] = Fold(src[i]);
        });
    }

    /// <summary>True for characters that separate words in names and paths.</summary>
    public static bool IsSeparator(char c) =>
        c is ' ' or '-' or '_' or '.' or '/' or '\\' or '(' or ')' or '[' or ']' or ',' or ';' or ':' or '+' or '@' or '&';
}
