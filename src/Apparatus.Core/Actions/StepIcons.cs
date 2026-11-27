using Apparatus.Core.Search.Providers;

namespace Apparatus.Core.Actions;

/// <summary>Which application icon stands for the target of an "open application" step.</summary>
public static class StepIcons
{
    private const string AppsFolder = @"shell:AppsFolder\";

    /// <summary>
    /// The icon key of what <paramref name="target"/> opens (see <see cref="AppEntry.IconKey"/>), or null to keep the glyph of the
    /// step: an installed application, a file given by its full path, or a program written by name alone ("notepad.exe", "calc")
    /// found in <paramref name="programFolders"/>. Reads the disk: do not call it on the interface thread.
    /// </summary>
    public static string? KeyFor(IEnumerable<AppEntry> apps, string? target, Func<string, bool> fileExists, IEnumerable<string> programFolders)
    {
        string text = (target ?? "").Trim().Trim('"').Trim();
        if (text.Length == 0) return null;

        var app = apps.FirstOrDefault(a => a.ParsingName.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (app is not null) return app.IconKey;

        // Windows finds a packaged application by itself, also while the list of applications is still loading.
        if (text.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase)) return "shell:" + text;

        if (Path.IsPathFullyQualified(text)) return fileExists(text) ? "shell:" + text : null;

        // A relative path or an address ("https://…") has no program to look for.
        if (text.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) return null;

        string file = Path.HasExtension(text) ? text : text + ".exe";
        foreach (string folder in programFolders)
        {
            if (!Path.IsPathFullyQualified(folder)) continue;
            string path = Path.Combine(folder, file);
            if (fileExists(path)) return "shell:" + path;
        }
        return null;
    }
}
