using Apparatus.Core;
using Apparatus.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Apparatus.App;

public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Must come first: when the installer or updater starts this program with one of its hooks (install, update, uninstall)
        // Velopack runs it and exits; on an ordinary start it does nothing.
        Velopack.VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                // The start-up entry must not outlive the program. Settings and data in %LOCALAPPDATA%\Apparatus are left alone.
                try { AutoStart.Set(false); } catch (Exception) { /* nothing more to do while uninstalling */ }
            })
            .Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();

        using var instance = SingleInstance.Acquire();
        if (!instance.IsFirst)
        {
            // Already running: ask that instance to show the launcher, then leave.
            instance.SignalFirstInstance();
            return 0;
        }

        AppLog.Initialize(AppPaths.LogsDirectory);
        AppLog.Info($"Apparatus {typeof(Program).Assembly.GetName().Version?.ToString(3)} starting (args: {string.Join(' ', args)})");

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App(instance, args);
        });
        return 0;
    }
}
