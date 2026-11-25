using Apparatus.Core.About;

namespace Apparatus.Core.Tests;

public class ThirdPartyLicensesTests
{
    private readonly ThirdPartyLicenses _licenses = ThirdPartyLicenses.LoadEmbedded();

    [Fact]
    public void TheEmbeddedList_HasTheComponentsTheInstallerCarries()
    {
        Assert.True(_licenses.Packages.Count >= 20);
        foreach (string expected in new[] { "Velopack", "OpenAI", "Windows App SDK", ".NET runtime", "Microsoft.Web.WebView2", "SQLitePCLRaw.core" })
            Assert.Contains(_licenses.Packages, p => p.Name == expected);
        Assert.Equal(_licenses.Packages.Count, _licenses.Packages.Select(p => p.Name).Distinct().Count());
    }

    [Fact]
    public void EveryPackage_HasEverythingTheDialogShows()
    {
        foreach (var p in _licenses.Packages)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Version), p.Name);
            Assert.False(string.IsNullOrWhiteSpace(p.Author), p.Name);
            Assert.False(string.IsNullOrWhiteSpace(p.Copyright), p.Name);
            var license = _licenses.LicenseOf(p);
            Assert.True(license is not null && license.Text.Length > 40, $"{p.Name}: licença '{p.LicenseKey}' sem texto");
            Assert.StartsWith("https://", p.ProjectUrl ?? p.NuGetUrl ?? "", StringComparison.Ordinal);
            Assert.True(p.ProjectUrl is null || p.ProjectUrl.StartsWith("https://", StringComparison.Ordinal), p.Name);
            Assert.True(p.NuGetUrl is null || p.NuGetUrl.StartsWith("https://www.nuget.org/", StringComparison.Ordinal), p.Name);
        }
    }

    [Fact]
    public void TitleSubtitleAndFullText_AreWhatThePageShows()
    {
        var velopack = _licenses.Packages.Single(p => p.Name == "Velopack");
        Assert.Equal($"Velopack (v{velopack.Version})", velopack.Title);
        Assert.EndsWith("· MIT License", _licenses.Subtitle(velopack));
        string text = _licenses.FullText(velopack);
        Assert.StartsWith(velopack.Copyright, text);
        Assert.Contains("Permission is hereby granted, free of charge", text);

        var apache = _licenses.Packages.First(p => p.LicenseKey == "Apache-2.0");
        Assert.Contains("Apache License", _licenses.FullText(apache));
        Assert.Contains("Version 2.0, January 2004", _licenses.FullText(apache));
    }

    [Fact]
    public void TheWindowsAppSdk_ShowsMicrosoftsTerms_NotAnOpenSourceLicense()
    {
        var sdk = _licenses.Packages.Single(p => p.Name == "Windows App SDK");
        Assert.Equal("Microsoft Software License Terms", _licenses.LicenseOf(sdk)!.Name);
        Assert.Contains("DISTRIBUTABLE CODE", _licenses.FullText(sdk));
    }

    [Fact]
    public void ThePeopleReadableList_ListsTheSamePackagesAsTheAboutPage()
    {
        // THIRD-PARTY-NOTICES.md is what the installer carries; this list is what the About page shows. They must not drift apart.
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "THIRD-PARTY-NOTICES.md"))) dir = Path.GetDirectoryName(dir);
        if (dir is null) return;   // not running inside a checkout of the repository

        string notices = File.ReadAllText(Path.Combine(dir, "THIRD-PARTY-NOTICES.md"));
        foreach (var p in _licenses.Packages)
        {
            // The notices file groups the Windows App SDK packages and writes the runtime as "10.0.x".
            if (p.Name is "Windows App SDK" or ".NET runtime")
            {
                Assert.Contains(p.Name is "Windows App SDK" ? "Microsoft.WindowsAppSDK" : ".NET runtime", notices);
                continue;
            }
            Assert.True(notices.Contains($"| {p.Name}") && notices.Contains(p.Version), $"{p.Name} {p.Version} não consta em THIRD-PARTY-NOTICES.md");
        }
    }
}
