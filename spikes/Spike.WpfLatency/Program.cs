// Spike 3b: how long does a pre-created, hidden WPF launcher window take to become visible with a rendered frame?
// Measures "Show() -> first rendered frame". Hotkey delivery itself was ~1 ms (see Spike.Hotkeys).
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.ThemeMode = ThemeMode.System;   // Fluent theme (.NET 9+/10, experimental)

        var swCold = Stopwatch.StartNew();
        var win = BuildWindow();
        double constructMs = swCold.Elapsed.TotalMilliseconds;

        var samples = new List<double>();
        int iteration = -1;
        Stopwatch sw = new();
        EventHandler? onRender = null;
        double coldShow = 0;

        void Next()
        {
            iteration++;
            if (iteration > 15)
            {
                CompositionTarget.Rendering -= onRender;
                win.Close();
                var warm = samples.Skip(1).OrderBy(x => x).ToList();
                Console.WriteLine($"WPF (Fluent ThemeMode=System, .NET {Environment.Version})");
                Console.WriteLine($"  construct window:        {constructMs:F1} ms");
                Console.WriteLine($"  first Show -> frame:     {coldShow:F1} ms (cold)");
                Console.WriteLine($"  warm Show -> frame (n={warm.Count}): min {warm.First():F1} | median {warm[warm.Count / 2]:F1} | max {warm.Last():F1} ms");
                app.Shutdown();
                return;
            }
            sw.Restart();
            win.Show();
            win.Activate();
        }

        onRender = (_, _) =>
        {
            if (!sw.IsRunning) return;
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds;
            samples.Add(ms);
            if (iteration == 0) coldShow = ms;
            // hide, wait, show again
            win.Hide();
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            t.Tick += (_, _) => { t.Stop(); Next(); };
            t.Start();
        };
        CompositionTarget.Rendering += onRender;

        app.Startup += (_, _) => Next();
        app.Run();
    }

    static Window BuildWindow()
    {
        var list = new ListBox { Margin = new Thickness(0, 8, 0, 0) };
        for (int i = 1; i <= 8; i++) list.Items.Add($"Resultado {i}  —  C:\\Programas\\App{i}.exe");
        var box = new TextBox { FontSize = 20, Padding = new Thickness(8) };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(box);
        panel.Children.Add(list);
        return new Window
        {
            Width = 640, Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Content = panel,
        };
    }
}
