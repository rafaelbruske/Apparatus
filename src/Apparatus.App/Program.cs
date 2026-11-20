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
        WinRT.ComWrappersSupport.InitializeComWrappers();

        using var instance = SingleInstance.Acquire();
        if (!instance.IsFirst)
        {
            // Already running: ask that instance to show the launcher, then leave.
            instance.SignalFirstInstance();
            return 0;
        }

        AppLog.Initialize(AppPaths.LogsDirectory);
        AppLog.Info($"Apparatus starting (args: {string.Join(' ', args)})");

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App(instance, args);
        });
        return 0;
    }
}
