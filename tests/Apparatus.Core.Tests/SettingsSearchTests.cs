using Apparatus.Core.Settings;

namespace Apparatus.Core.Tests;

public class SettingsSearchTests
{
    [Fact]
    public void Normalize_DropsCaseAndAccents() => Assert.Equal("area de transferencia", SettingsSearch.Normalize("Área de Transferência"));

    [Fact]
    public void Words_SplitsAndNormalizes()
    {
        Assert.Equal(new[] { "botao", "direito" }, SettingsSearch.Words("  Botão   DIREITO "));
        Assert.Empty(SettingsSearch.Words("   "));
        Assert.Empty(SettingsSearch.Words(null));
    }

    [Theory]
    [InlineData("Iniciar com o Windows", "INICIAR", true)]
    [InlineData("Guardar um histórico do que eu copiar", "historico", true)]
    [InlineData("Menu do botão direito", "botao direito", true)]
    [InlineData("Menu do botão direito", "direito botao", true)]   // the order does not matter
    [InlineData("Flash ao fazer a captura automática", "flash windows", false)]   // every word is needed
    [InlineData("Flash ao fazer a captura automática", "", false)]
    public void Matches_NeedsEveryWordAndIgnoresCaseAndAccents(string text, string query, bool expected) =>
        Assert.Equal(expected, SettingsSearch.Matches(text, SettingsSearch.Words(query)));

    private static readonly string[] Order = { "general", "snippets-list", "actions-list", "snippets", "capture" };

    [Fact]
    public void PageToJumpTo_StaysWhenThePageOnTheScreenHasMatches() =>
        Assert.Null(SettingsSearch.PageToJumpTo("capture", Order, new Dictionary<string, int> { ["general"] = 1, ["capture"] = 2 }));

    [Fact]
    public void PageToJumpTo_GoesToTheFirstSettingsPageWithMatches() =>
        Assert.Equal("snippets", SettingsSearch.PageToJumpTo("general", Order, new Dictionary<string, int> { ["capture"] = 1, ["snippets"] = 1 }));

    [Fact]
    public void PageToJumpTo_SkipsTheEditorPagesListedBeforeTheMatch() =>
        // "gatilho" matches the snippets editor by keyword, which comes first in the side bar, but only the settings page has blocks.
        Assert.Equal("snippets", SettingsSearch.PageToJumpTo("general", Order, new Dictionary<string, int> { ["snippets"] = 1 }));

    [Fact]
    public void PageToJumpTo_StaysWhenNoSettingMatches() =>
        // "macro" matches only the actions editor.
        Assert.Null(SettingsSearch.PageToJumpTo("general", Order, new Dictionary<string, int>()));

    [Fact]
    public void FindRanges_PointsIntoTheOriginalTextIgnoringCaseAndAccents()
    {
        const string text = "Atalho da captura automática (tela inteira)";
        var ranges = SettingsSearch.FindRanges(text, SettingsSearch.Words("automatica INTEIRA"));

        Assert.Equal(2, ranges.Count);
        Assert.Equal((text.IndexOf("automática"), "automática".Length), ranges[0]);
        Assert.Equal((text.IndexOf("inteira"), "inteira".Length), ranges[1]);
    }

    [Fact]
    public void FindRanges_FindsEveryOccurrence()
    {
        var ranges = SettingsSearch.FindRanges("ia, de ia", SettingsSearch.Words("ia"));
        Assert.Equal(new[] { (0, 2), (7, 2) }, ranges.ToArray());
    }

    [Fact]
    public void FindRanges_MergesWordsThatOverlap()
    {
        // "capt" and "ptur" overlap in "captura": one range, "captur".
        var merged = SettingsSearch.FindRanges("captura", SettingsSearch.Words("capt ptur"));
        Assert.Equal(new[] { (0, 6) }, merged.ToArray());
    }

    [Fact]
    public void FindRanges_IsEmptyWithoutWordsOrMatches()
    {
        Assert.Empty(SettingsSearch.FindRanges("texto", SettingsSearch.Words("")));
        Assert.Empty(SettingsSearch.FindRanges("texto", SettingsSearch.Words("zzz")));
        Assert.Empty(SettingsSearch.FindRanges("", SettingsSearch.Words("a")));
    }
}
