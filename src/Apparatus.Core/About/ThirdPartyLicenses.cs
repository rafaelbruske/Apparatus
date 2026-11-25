using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apparatus.Core.About;

/// <summary>One third-party component the installed program contains, as listed in the About page.</summary>
public sealed record LicensedPackage(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("license")] string LicenseKey,
    [property: JsonPropertyName("copyright")] string Copyright,
    [property: JsonPropertyName("projectUrl")] string? ProjectUrl,
    [property: JsonPropertyName("nugetUrl")] string? NuGetUrl)
{
    /// <summary>"Name (v1.2.3)".</summary>
    public string Title => $"{Name} (v{Version})";
}

/// <summary>A license text, shared by every package that uses it.</summary>
public sealed record LicenseInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("text")] string Text);

/// <summary>
/// The open source components that Apparatus ships with and their licenses (third-party-licenses.json, embedded). The same list
/// is written for people in THIRD-PARTY-NOTICES.md, which the installer carries; a test keeps the two in step.
/// </summary>
public sealed class ThirdPartyLicenses
{
    private sealed record Document(
        [property: JsonPropertyName("packages")] List<LicensedPackage> Packages,
        [property: JsonPropertyName("licenses")] Dictionary<string, LicenseInfo> Licenses);

    private readonly Dictionary<string, LicenseInfo> _licenses;

    public ThirdPartyLicenses(IEnumerable<LicensedPackage> packages, IDictionary<string, LicenseInfo> licenses)
    {
        Packages = packages.ToList();
        _licenses = new Dictionary<string, LicenseInfo>(licenses, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<LicensedPackage> Packages { get; }

    /// <summary>The license of a package, or null when its key is unknown.</summary>
    public LicenseInfo? LicenseOf(LicensedPackage package) => _licenses.GetValueOrDefault(package.LicenseKey);

    /// <summary>"Microsoft · MIT License", for the line under the package name.</summary>
    public string Subtitle(LicensedPackage package) => $"{package.Author} · {LicenseOf(package)?.Name ?? package.LicenseKey}";

    /// <summary>The full text to show: the package's copyright notice, then the license.</summary>
    public string FullText(LicensedPackage package)
    {
        string body = LicenseOf(package)?.Text ?? "";
        return package.Copyright.Length == 0 ? body : $"{package.Copyright}\n\n{body}";
    }

    public static ThirdPartyLicenses LoadEmbedded()
    {
        using var stream = typeof(ThirdPartyLicenses).Assembly.GetManifestResourceStream("third-party-licenses.json")
            ?? throw new InvalidOperationException("third-party-licenses.json is not embedded");
        return Parse(stream);
    }

    public static ThirdPartyLicenses Parse(Stream json)
    {
        var doc = JsonSerializer.Deserialize<Document>(json) ?? throw new InvalidDataException("The licenses file is empty.");
        return new ThirdPartyLicenses(doc.Packages, doc.Licenses);
    }
}
