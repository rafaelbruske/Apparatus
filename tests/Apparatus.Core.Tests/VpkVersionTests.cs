using System.Text.RegularExpressions;

namespace Apparatus.Core.Tests;

public class VpkVersionTests
{
    /// <summary>Every file that tells someone (a workflow or a person) which version of the vpk tool to install.</summary>
    private static readonly string[] FilesThatInstallVpk =
    {
        ".github/workflows/ci.yml", ".github/workflows/release.yml", "build/release.ps1", "README.md", "README.pt-BR.md",
    };

    [Fact]
    public void TheVpkTool_IsTheVersionOfTheVelopackPackageTheAppUses()
    {
        // The tool packs what the library inside the app later reads. An update of the package alone (a Dependabot pull request,
        // for one) must fail here until the tool is updated with it.
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Apparatus.sln"))) dir = Path.GetDirectoryName(dir);
        if (dir is null) return;   // not running inside a checkout of the repository

        string project = File.ReadAllText(Path.Combine(dir, "src/Apparatus.App/Apparatus.App.csproj"));
        var package = Regex.Match(project, "Include=\"Velopack\" Version=\"([^\"]+)\"");
        Assert.True(package.Success, "The Velopack package reference was not found in Apparatus.App.csproj");

        foreach (string file in FilesThatInstallVpk)
        {
            var installs = Regex.Matches(File.ReadAllText(Path.Combine(dir, file)), @"vpk\s+--version\s+(\d+(?:\.\d+)+)");
            Assert.True(installs.Count > 0, $"{file} no longer says which version of vpk to install: update this test");
            foreach (Match install in installs)
                Assert.True(install.Groups[1].Value == package.Groups[1].Value,
                    $"{file} installs vpk {install.Groups[1].Value}, but the app uses Velopack {package.Groups[1].Value}");
        }
    }
}
