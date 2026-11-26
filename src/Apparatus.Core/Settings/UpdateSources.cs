namespace Apparatus.Core.Settings;

/// <summary>
/// The places the app can look for updates. A GitHub repository address is asked through the GitHub API ("which is the newest
/// release?"), so the app compares the versions itself and sends nothing about the installed one. Any other address is a plain web
/// address that holds the release files, and a folder works too.
/// </summary>
public static class UpdateSources
{
    /// <summary>The repository the official releases are published to (see docs/LANCAMENTO.md).</summary>
    public const string OfficialGithubRepository = "https://github.com/rafaelbruske/Apparatus";

    /// <summary>
    /// True for the address of a repository itself (https://github.com/owner/repo, with or without a trailing slash or ".git"),
    /// and false for a deeper address such as .../releases/latest/download, which is a plain web address.
    /// </summary>
    public static bool IsGithubRepository(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        if (!Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        string repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return parts[0].Length > 0 && repo.Length > 0;
    }
}
