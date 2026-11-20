namespace Apparatus.App;

/// <summary>
/// Where Apparatus keeps its files. Setting the environment variable <c>APPARATUS_PROFILE_DIR</c> moves everything
/// into that folder (<c>data</c> and <c>config</c> inside it) — used for isolated test runs and portable setups.
/// </summary>
public static class AppPaths
{
    private static readonly string? ProfileOverride = Environment.GetEnvironmentVariable("APPARATUS_PROFILE_DIR") is { Length: > 0 } p ? p : null;

    private static string Local => ProfileOverride is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apparatus")
        : Path.Combine(ProfileOverride, "data");

    private static string Roaming => ProfileOverride is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Apparatus")
        : Path.Combine(ProfileOverride, "config");

    /// <summary>Cache and databases: index snapshots, history, logs.</summary>
    public static string DataDirectory => Local;

    public static string LogsDirectory => Path.Combine(Local, "logs");

    public static string IndexDirectory => Path.Combine(Local, "index");

    public static string DatabaseFile => Path.Combine(Local, "apparatus.db");

    /// <summary>User-editable configuration.</summary>
    public static string SettingsFile => Path.Combine(Roaming, "settings.json");

    /// <summary>User-written snippets: user data, kept apart from the disposable cache.</summary>
    public static string SnippetsFile => Path.Combine(Roaming, "snippets.db");

    public static string AssetPath(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);
}
