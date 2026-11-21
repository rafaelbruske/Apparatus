namespace Apparatus.Core.Search.Providers;

/// <summary>
/// Internet shortcuts (<c>.url</c>) in the Start Menu, by name. Steam and other launchers register their games as apps whose
/// "path" is a URL (<c>steam://rungameid/…</c>): the shell has no icon for that, but the shortcut file that created the entry does
/// (its <c>IconFile</c>), and asking the shell for the shortcut's own icon returns it.
/// </summary>
public sealed class UrlShortcutIndex
{
    private readonly Dictionary<string, string> _byName = new(StringComparer.CurrentCultureIgnoreCase);

    public UrlShortcutIndex(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.url", SearchOption.AllDirectories))
                    _byName.TryAdd(Path.GetFileNameWithoutExtension(file), file);   // the first one found wins
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Scanning '{root}' for shortcuts failed: {ex.Message}");
            }
        }
    }

    public int Count => _byName.Count;

    /// <summary>The shortcut file whose name is the app's name, or null.</summary>
    public string? Find(string appName) => _byName.TryGetValue(appName, out var path) ? path : null;

    /// <summary>
    /// The file named by the <c>IconFile=</c> line of the app's shortcut, if the shortcut exists and that file does. (Asking the shell
    /// for the icon of the .url itself does not work: it answers with the generic document icon.)
    /// </summary>
    public string? FindIconFile(string appName)
    {
        if (Find(appName) is not { } shortcut) return null;
        try
        {
            foreach (var line in File.ReadLines(shortcut))
            {
                if (!line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) continue;
                string file = Environment.ExpandEnvironmentVariables(line["IconFile=".Length..].Trim().Trim('"'));
                return file.Length > 0 && File.Exists(file) ? file : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Reading shortcut '{shortcut}' failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>The folders that make up the Start Menu's program list: the user's and the shared one.</summary>
    public static IEnumerable<string> StartMenuRoots() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
    }.Where(p => p.Length > 0);
}
