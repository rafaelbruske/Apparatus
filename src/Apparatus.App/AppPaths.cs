namespace Apparatus.App;

public static class AppPaths
{
    /// <summary>Cache and databases: index snapshots, history, logs.</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apparatus");

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static string IndexDirectory => Path.Combine(DataDirectory, "index");

    public static string DatabaseFile => Path.Combine(DataDirectory, "apparatus.db");

    /// <summary>User-editable configuration.</summary>
    public static string SettingsFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Apparatus", "settings.json");

    public static string AssetPath(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);
}
