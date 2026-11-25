using System.Diagnostics;
using Apparatus.Core;
using Apparatus.Core.Start;

namespace Apparatus.Native;

/// <summary>One entry of the "Tasks" of an app's jump list: what to show and what to start.</summary>
public sealed record JumpTask(string Title, string Target, string Arguments, string WorkingDirectory, string IconFile = "", int IconIndex = 0)
{
    /// <summary>Key for the icon cache ("res:file|number"), or null when the task names no icon.</summary>
    public string? IconKey => IconFile.Length > 0 ? $"res:{IconFile}|{IconIndex}" : null;

    /// <summary>Starts it the way the taskbar would.</summary>
    public void Run()
    {
        var info = new ProcessStartInfo(Target) { UseShellExecute = true };
        if (Arguments.Length > 0) info.Arguments = Arguments;
        if (WorkingDirectory.Length > 0 && Directory.Exists(WorkingDirectory)) info.WorkingDirectory = WorkingDirectory;
        Process.Start(info)?.Dispose();
    }
}

/// <summary>Reads the tasks of an app's jump list. The shell does the parsing of the shortcuts, so this runs on an STA thread.</summary>
public static class JumpTaskReader
{
    public static Task<IReadOnlyList<JumpTask>> ReadAsync(string appId)
    {
        var done = new TaskCompletionSource<IReadOnlyList<JumpTask>>();
        var thread = new Thread(() =>
        {
            try { done.SetResult(Read(appId)); }
            catch (Exception ex)
            {
                AppLog.Warn($"Reading the jump list of {appId} failed: {ex.Message}");
                done.SetResult(Array.Empty<JumpTask>());
            }
        }) { IsBackground = true, Name = "Jump list reader" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    /// <summary>"C:\\App\\app.exe,3" gives the file and the number; an environment variable in the path is expanded.</summary>
    internal static (string File, int Index) SplitIcon(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return ("", 0);
        string file = location.Trim();
        int index = 0;
        int comma = file.LastIndexOf(',');
        if (comma > 0 && int.TryParse(file[(comma + 1)..].Trim(), out int parsed))
        {
            index = parsed;
            file = file[..comma];
        }
        file = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
        return (File.Exists(file) ? file : "", index);
    }

    private static IReadOnlyList<JumpTask> Read(string appId)
    {
        string file = JumpLists.FileFor(appId);
        if (!File.Exists(file)) return Array.Empty<JumpTask>();
        var shortcuts = JumpLists.TaskShortcuts(File.ReadAllBytes(file));
        if (shortcuts.Count == 0) return Array.Empty<JumpTask>();

        string folder = Path.Combine(Path.GetTempPath(), "apparatus-jl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Shell.Application unavailable");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic directory = shell.NameSpace(folder);
            var wshType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
            dynamic wsh = Activator.CreateInstance(wshType)!;

            var tasks = new List<JumpTask>();
            for (int i = 0; i < shortcuts.Count; i++)
            {
                string name = $"task{i}.lnk";
                File.WriteAllBytes(Path.Combine(folder, name), shortcuts[i]);
                try
                {
                    dynamic item = directory.ParseName(name);
                    dynamic link = item.GetLink;
                    string title = ((string?)item.ExtendedProperty("System.Title"))?.Trim() ?? "";
                    string target = (string?)link.Path ?? "";
                    if (title.Length == 0) title = ((string?)link.Description)?.Trim() ?? "";
                    if (title.Length == 0 || target.Length == 0) continue;
                    var (iconFile, iconIndex) = SplitIcon((string?)wsh.CreateShortcut(Path.Combine(folder, name)).IconLocation);
                    tasks.Add(new JumpTask(title, target, (string?)link.Arguments ?? "", (string?)link.WorkingDirectory ?? "", iconFile, iconIndex));
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"A task of the jump list of {appId} could not be read: {ex.Message}");
                }
            }
            return tasks;
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}

