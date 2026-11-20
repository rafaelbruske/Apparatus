using Apparatus.Core.Text;

namespace Apparatus.Core.Search;

/// <summary>A parsed query: whitespace-separated terms, each folded (case/accent-insensitive).</summary>
public sealed class FuzzyQuery
{
    public const int MaxTerms = 5;

    public string Raw { get; }
    public string Folded { get; }
    internal char[][] Terms { get; }
    public bool IsEmpty => Terms.Length == 0;
    public int TotalLength { get; }

    private FuzzyQuery(string raw, string folded, char[][] terms)
    {
        Raw = raw;
        Folded = folded;
        Terms = terms;
        TotalLength = terms.Sum(t => t.Length);
    }

    public static FuzzyQuery Parse(string? text)
    {
        text ??= string.Empty;
        var folded = TextFold.Fold(text.Trim());
        var terms = folded
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Take(MaxTerms)
            .Select(t => t.ToCharArray())
            .ToArray();
        return new FuzzyQuery(text, folded, terms);
    }
}

/// <summary>
/// fzf-style subsequence matcher: finds the tightest window containing the term as a subsequence,
/// then rewards matches at word starts and consecutive runs. Every term must match (AND).
/// </summary>
public static class FuzzyMatcher
{
    public const int NoMatch = int.MinValue;

    private const int MatchScore = 16;
    private const int GapStart = -3;
    private const int GapExtension = -1;
    private const int BonusFirstChar = 12;
    private const int BonusAfterSeparator = 8;
    private const int BonusCamelOrDigit = 5;
    private const int BonusConsecutive = 4;
    private const int BonusExactName = 60;
    private const double MinPerCharShort = 14.5;
    private const double MinPerCharLong = 18.0;

    /// <summary>Score of <paramref name="text"/> against the query, or <see cref="NoMatch"/>.</summary>
    public static int Score(ReadOnlySpan<char> text, FuzzyQuery query)
    {
        var terms = query.Terms;
        if (terms.Length == 0) return 0;
        int total = 0;
        for (int i = 0; i < terms.Length; i++)
        {
            int s = ScoreTerm(text, terms[i], default);
            if (s == NoMatch) return NoMatch;
            total += s;
        }
        int result = total - Math.Min(text.Length, 64) / 8;

        // Quality floor: letters scattered across a long name ("wifi" in "Windows Software Development Kit")
        // satisfy the subsequence test but are noise. Short queries are often initials, so they get more slack.
        int length = query.TotalLength;
        double minPerChar = length <= 3 ? MinPerCharShort : MinPerCharLong;
        return result < minPerChar * length ? NoMatch : result;
    }

    /// <summary>Indices (in <paramref name="text"/>) of the characters matched by the query.</summary>
    public static IReadOnlyList<int> Highlight(string text, FuzzyQuery query)
    {
        var result = new List<int>();
        Span<int> positions = stackalloc int[64];
        foreach (var term in query.Terms)
        {
            if (term.Length > positions.Length) continue;
            int s = ScoreTerm(text, term, positions);
            if (s == NoMatch) continue;
            for (int i = 0; i < term.Length; i++) result.Add(positions[i]);
        }
        result.Sort();
        return result;
    }

    private static int ScoreTerm(ReadOnlySpan<char> text, ReadOnlySpan<char> term, Span<int> positions)
    {
        int n = text.Length, m = term.Length;
        if (m == 0) return 0;
        if (m > n) return NoMatch;

        // 1. Forward: does the term occur as a subsequence at all, and where does the earliest match end?
        int ti = 0, end = -1;
        for (int i = 0; i < n; i++)
        {
            if (TextFold.Fold(text[i]) == term[ti] && ++ti == m) { end = i; break; }
        }
        if (end < 0) return NoMatch;

        // 2. Candidate alignments: the tightest window, the earliest start, and the first word-start occurrence
        //    ("gc" should pick the G of "Google", not the g inside it).
        int tj = m - 1, tight = end;
        for (int i = end; i >= 0; i--)
        {
            if (TextFold.Fold(text[i]) == term[tj] && --tj < 0) { tight = i; break; }
        }
        int earliest = -1, boundary = -1;
        for (int i = 0; i < n; i++)
        {
            if (TextFold.Fold(text[i]) != term[0]) continue;
            if (earliest < 0) earliest = i;
            if (BonusAt(text, i) >= BonusAfterSeparator) { boundary = i; break; }
        }

        Span<int> scratch = positions.IsEmpty ? default : stackalloc int[Math.Min(m, 64)];
        int best = NoMatch;
        TryAlignment(text, term, tight, positions, scratch, ref best);
        if (earliest >= 0 && earliest != tight) TryAlignment(text, term, earliest, positions, scratch, ref best);
        if (boundary >= 0 && boundary != tight && boundary != earliest) TryAlignment(text, term, boundary, positions, scratch, ref best);
        return best;
    }

    /// <summary>Scores the greedy forward match starting at <paramref name="start"/>; keeps it if it beats <paramref name="best"/>.</summary>
    private static void TryAlignment(ReadOnlySpan<char> text, ReadOnlySpan<char> term, int start,
        Span<int> bestPositions, Span<int> scratch, ref int best)
    {
        int n = text.Length, m = term.Length;
        int score = 0, prev = -2, chain = 0, k = 0;
        for (int i = start; i < n && k < m; i++)
        {
            if (TextFold.Fold(text[i]) != term[k]) continue;
            int bonus = BonusAt(text, i);
            if (i == prev + 1)
            {
                chain = Math.Max(Math.Max(chain, bonus), BonusConsecutive);
                bonus = chain;
            }
            else
            {
                chain = bonus;
                if (prev >= 0) score += GapStart + (i - prev - 2) * GapExtension;
            }
            score += MatchScore + bonus;
            if (!scratch.IsEmpty && k < scratch.Length) scratch[k] = i;
            prev = i;
            k++;
        }
        if (k < m) return;
        if (start == 0 && prev == m - 1 && n == m) score += BonusExactName;
        if (score <= best) return;
        best = score;
        if (!bestPositions.IsEmpty) scratch[..Math.Min(m, scratch.Length)].CopyTo(bestPositions);
    }

    private static int BonusAt(ReadOnlySpan<char> text, int i)
    {
        if (i == 0) return BonusFirstChar;
        char prev = text[i - 1], cur = text[i];
        if (TextFold.IsSeparator(prev)) return BonusAfterSeparator;
        if (char.IsLower(prev) && char.IsUpper(cur)) return BonusCamelOrDigit;
        if (char.IsLetter(prev) && char.IsDigit(cur)) return BonusCamelOrDigit;
        return 0;
    }
}
