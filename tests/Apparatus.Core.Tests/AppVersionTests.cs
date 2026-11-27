using System.Text.RegularExpressions;

namespace Apparatus.Core.Tests;

public class AppVersionTests
{
    [Fact]
    public void TheVersion_IsWrittenOnce_InDirectoryBuildProps()
    {
        // build\get-version.ps1 and the release workflow read it from there. A <Version> in a project file would silently replace it
        // in the program, so the release would say one number and the program another.
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Apparatus.sln"))) dir = Path.GetDirectoryName(dir);
        if (dir is null) return;   // not running inside a checkout of the repository

        var versions = Regex.Matches(File.ReadAllText(Path.Combine(dir, "Directory.Build.props")), @"<Version>([^<]*)</Version>");
        Assert.True(versions.Count == 1, $"Directory.Build.props must have exactly one <Version>, found {versions.Count}");
        Assert.Matches(@"^\d+\.\d+\.\d+$", versions[0].Groups[1].Value);

        foreach (string folder in new[] { "src", "tests" })
            foreach (string project in Directory.EnumerateFiles(Path.Combine(dir, folder), "*.csproj", SearchOption.AllDirectories))
                Assert.False(Regex.IsMatch(File.ReadAllText(project), @"<(Version|VersionPrefix|AssemblyVersion|FileVersion)>"),
                    $"{Path.GetFileName(project)} sets its own version: remove it, the version lives in Directory.Build.props");
    }
}
