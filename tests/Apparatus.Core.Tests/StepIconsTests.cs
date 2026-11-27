using Apparatus.Core.Actions;
using Apparatus.Core.Search.Providers;

namespace Apparatus.Core.Tests;

public class StepIconsTests
{
    private static readonly AppEntry[] Apps =
    {
        new("player", "Player", @"C:\Programs\Player\Player.exe"),
        new("com.discord", "Discord", @"shell:AppsFolder\com.discord"),
        new("steam://rungameid/1", "Jogo", @"shell:AppsFolder\steam://rungameid/1", @"path:C:\Jogos\jogo.exe"),
        new("epic://x", "Sem ícone", @"shell:AppsFolder\epic://x", ""),
    };

    private static readonly string[] Disk = { @"C:\Windows\System32\notepad.exe", @"C:\Windows\System32\calc.exe", @"D:\Docs\nota.txt" };

    private static readonly string[] Folders = { "", ".", @"C:\Windows\System32" };

    private static string? Key(string? target) =>
        StepIcons.KeyFor(Apps, target, path => Disk.Contains(path, StringComparer.OrdinalIgnoreCase), Folders);

    [Theory]
    [InlineData(@"shell:AppsFolder\com.discord", @"shell:shell:AppsFolder\com.discord")]
    [InlineData(@"c:\programs\player\PLAYER.exe", @"shell:C:\Programs\Player\Player.exe")]
    [InlineData(@"shell:AppsFolder\steam://rungameid/1", @"path:C:\Jogos\jogo.exe")]
    public void AnInstalledApplication_UsesItsOwnIcon(string target, string expected) => Assert.Equal(expected, Key(target));

    [Theory]
    [InlineData(@"D:\Docs\nota.txt", @"shell:D:\Docs\nota.txt")]
    [InlineData("  \"D:\\Docs\\nota.txt\" ", @"shell:D:\Docs\nota.txt")]
    [InlineData("notepad.exe", @"shell:C:\Windows\System32\notepad.exe")]
    [InlineData("calc", @"shell:C:\Windows\System32\calc.exe")]
    [InlineData(@"shell:AppsFolder\ainda.nao.listado", @"shell:shell:AppsFolder\ainda.nao.listado")]
    public void AFileAProgramNameOrAPackagedApp_HasAnIconToo(string target, string expected) => Assert.Equal(expected, Key(target));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"\"")]
    [InlineData(@"shell:AppsFolder\epic://x")]        // installed, with no icon to be had
    [InlineData(@"D:\Docs\sumiu.exe")]
    [InlineData(@"Docs\nota.txt")]
    [InlineData("https://exemplo.com")]
    [InlineData("shell:startup")]
    [InlineData("programa que nao existe")]
    public void Otherwise_TheStepKeepsItsGlyph(string? target) => Assert.Null(Key(target));
}
