using Apparatus.Core.Search;
using Apparatus.Core.Text;

namespace Apparatus.Core.Tests;

public class FuzzyMatcherTests
{
    private static int Score(string text, string query) => FuzzyMatcher.Score(text, FuzzyQuery.Parse(query));

    [Fact]
    public void Folding_IgnoresCaseAndAccents()
    {
        Assert.Equal("configuracao", TextFold.Fold("Configuração"));
        Assert.Equal("acucar", TextFold.Fold("AÇÚCAR"));
    }

    [Theory]
    [InlineData("Configuração", "configuracao")]
    [InlineData("Google Chrome", "gc")]
    [InlineData("Visual Studio Code", "vsc")]
    [InlineData("PhotoShop", "ps")]
    public void Matches_Subsequences(string text, string query) =>
        Assert.NotEqual(FuzzyMatcher.NoMatch, Score(text, query));

    [Theory]
    [InlineData("Chrome", "chx")]
    [InlineData("abc", "abcd")]
    [InlineData("Notepad", "zz")]
    public void Rejects_NonSubsequences(string text, string query) =>
        Assert.Equal(FuzzyMatcher.NoMatch, Score(text, query));

    [Fact]
    public void Prefix_BeatsMidWordMatch() =>
        Assert.True(Score("Chrome", "chr") > Score("Archive", "chr"));

    [Fact]
    public void WordBoundary_BeatsScatteredMatch() =>
        Assert.True(Score("Google Chrome", "gc") > Score("Magic Cube Player", "gc"));

    [Fact]
    public void Exact_BeatsLongerName() =>
        Assert.True(Score("Calculadora", "calculadora") > Score("Calculadora Científica Avançada", "calculadora"));

    [Fact]
    public void ConsecutiveRun_BeatsGaps() =>
        Assert.True(Score("notepad", "note") > Score("n-o-t-e", "note"));

    [Fact]
    public void MultipleTerms_AllMustMatch()
    {
        Assert.NotEqual(FuzzyMatcher.NoMatch, Score("Relatório Final 2024.docx", "relat 2024"));
        Assert.Equal(FuzzyMatcher.NoMatch, Score("Relatório Final 2024.docx", "relat 2023"));
    }

    [Fact]
    public void Highlight_ReturnsMatchedPositions()
    {
        var positions = FuzzyMatcher.Highlight("Google Chrome", FuzzyQuery.Parse("gc"));
        Assert.Equal(new[] { 0, 7 }, positions);
    }

    [Theory]
    [InlineData("wifi", "Windows Software Development Kit")]            // letters scattered across words
    [InlineData("wifi", "Windows Defender Firewall com Segurança Avançada")]
    public void ScatteredLetters_AreRejected_ByTheQualityFloor(string query, string text) =>
        Assert.Equal(FuzzyMatcher.NoMatch, Score(text, query));

    [Theory]
    [InlineData("wifi", "Wi-Fi")]
    [InlineData("wifi", "WiFi-22.140.0-Driver64-Win10-Win11.exe")]
    [InlineData("vsc", "Visual Studio Code")]                           // initials stay allowed
    [InlineData("gc", "Google Chrome")]
    [InlineData("cmd", "Prompt de Comando")]
    [InlineData("pwsh", "PowerShell 7")]
    [InlineData("rel 2024", "Relatório Final 2024.docx")]
    public void GoodMatches_SurviveTheQualityFloor(string query, string text) =>
        Assert.NotEqual(FuzzyMatcher.NoMatch, Score(text, query));

    [Fact]
    public void EmptyQuery_MatchesEverything() =>
        Assert.Equal(0, FuzzyMatcher.Score("anything", FuzzyQuery.Parse("   ")));
}
