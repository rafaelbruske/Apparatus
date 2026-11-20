using System.Text;

namespace Apparatus.Core;

/// <summary>Minimal rolling file logger. Call <see cref="Initialize"/> once at startup.</summary>
public static class AppLog
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();
    private static string? _path;

    public static void Initialize(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, "apparatus.log");
            if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
            {
                File.Move(_path, Path.Combine(directory, "apparatus.old.log"), overwrite: true);
            }
        }
        catch
        {
            _path = null;
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var path = _path;
        if (path is null) return;
        try
        {
            lock (Gate)
            {
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
