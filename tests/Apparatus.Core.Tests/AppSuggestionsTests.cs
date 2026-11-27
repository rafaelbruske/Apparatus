using Apparatus.Core.Actions;
using Apparatus.Core.Search.Providers;

namespace Apparatus.Core.Tests;

public class AppSuggestionsTests
{
    private static readonly AppEntry[] Apps =
    {
        new(@"C:\Programs\BruskePlayer\BruskePlayer.App.exe", "BruskePlayer", @"C:\Programs\BruskePlayer\BruskePlayer.App.exe"),
        new("Microsoft.WindowsAlarms_8wekyb3d8bbwe!App", "Relógio", @"shell:AppsFolder\Microsoft.WindowsAlarms_8wekyb3d8bbwe!App"),
        new("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Calculadora", @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
        new("com.squirrel.Discord.Discord", "Discord", @"shell:AppsFolder\com.squirrel.Discord.Discord"),
        new("notepad", "Bloco de Notas", @"shell:AppsFolder\notepad"),
    };

    [Theory]
    [InlineData("disc", "Discord")]
    [InlineData("relogio", "Relógio")]            // without the accent
    [InlineData("Bruske Player", "BruskePlayer")] // the words typed apart
    [InlineData("  CALC ", "Calculadora")]
    public void Find_MatchesTheNameBeingTyped(string typed, string expected) =>
        Assert.Equal(expected, AppSuggestions.Find(Apps, typed)[0].Name);

    [Fact]
    public void Find_PutsTheNameThatStartsWithTheTypedTextFirst()
    {
        // "relog" is also scattered through "Reload Configuration"; the app whose name starts with it is the one meant.
        var apps = new[]
        {
            new AppEntry("reload", "Reload Configuration", @"shell:AppsFolder\reload"),
            new AppEntry("clock", "Relógio", @"shell:AppsFolder\clock"),
        };
        Assert.Equal("Relógio", AppSuggestions.Find(apps, "relog")[0].Name);
    }

    [Fact]
    public void Find_GivesTheTargetToStore()
    {
        var choice = Assert.Single(AppSuggestions.Find(Apps, "relogio"));
        Assert.Equal(@"shell:AppsFolder\Microsoft.WindowsAlarms_8wekyb3d8bbwe!App", choice.Target);
        Assert.Equal("Relógio", choice.ToString());
    }

    [Fact]
    public void Find_SaysWhereTheIconOfEachChoiceComesFrom()
    {
        var apps = new[]
        {
            new AppEntry("plain", "Jogo comum", @"shell:AppsFolder\plain"),
            new AppEntry("steam://rungameid/1", "Jogo da Steam", @"shell:AppsFolder\steam://rungameid/1", @"path:C:\Jogos\jogo.exe"),
            new AppEntry("epic://x", "Jogo sem ícone", @"shell:AppsFolder\epic://x", ""),
        };
        var icons = AppSuggestions.Find(apps, "jogo").ToDictionary(c => c.Name, c => c.IconKey);
        Assert.Equal(@"shell:shell:AppsFolder\plain", icons["Jogo comum"]);   // the usual one: the shell icon of what is opened
        Assert.Equal(@"path:C:\Jogos\jogo.exe", icons["Jogo da Steam"]);
        Assert.Null(icons["Jogo sem ícone"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\Programs\disc.exe")]
    [InlineData("D:")]
    [InlineData(@"\\server\share")]
    [InlineData(@"shell:AppsFolder\com.squirrel.Discord.Discord")]
    [InlineData("%LOCALAPPDATA%")]
    [InlineData("\"C:")]
    [InlineData("zzzz")]
    public void Find_SaysNothingForPathsEmptyTextOrUnknownNames(string? typed) => Assert.Empty(AppSuggestions.Find(Apps, typed));

    [Fact]
    public void Find_KeepsToTheLimit()
    {
        var many = Enumerable.Range(1, 30).Select(i => new AppEntry($"a{i}", $"App {i:00}", $@"shell:AppsFolder\a{i}")).ToList();
        Assert.Equal(AppSuggestions.MaxChoices, AppSuggestions.Find(many, "app").Count);
        Assert.Equal("App 01", AppSuggestions.Find(many, "app")[0].Name);   // equal scores: by name
    }

    [Fact]
    public void NameOf_KnowsAnInstalledTargetOnly()
    {
        Assert.Equal("Discord", AppSuggestions.NameOf(Apps, @" SHELL:appsfolder\com.squirrel.Discord.Discord "));
        Assert.Equal("BruskePlayer", AppSuggestions.NameOf(Apps, @"c:\programs\bruskeplayer\bruskeplayer.app.exe"));
        Assert.Null(AppSuggestions.NameOf(Apps, "calc.exe"));
        Assert.Null(AppSuggestions.NameOf(Apps, ""));
        Assert.Null(AppSuggestions.NameOf(Apps, null));
    }

    [Fact]
    public void TheStepReadsWithTheNameOfTheApp_AndStillValidates()
    {
        var step = ActionStep.Create(StepKind.OpenApp, ("target", @"shell:AppsFolder\com.squirrel.Discord.Discord"), ("name", "Discord"));
        Assert.Equal("Abrir Discord", StepCatalog.Summarize(step));
        Assert.Null(StepCatalog.Validate(step));
        step.Args.Remove("name");
        Assert.Contains("shell:AppsFolder", StepCatalog.Summarize(step));
    }
}
