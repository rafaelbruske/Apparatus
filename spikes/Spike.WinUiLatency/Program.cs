// Spike 3a: how long does a pre-created, hidden WinUI 3 launcher window take to become visible with a rendered frame?
// Measures "Show() -> first rendered frame" (CompositionTarget.Rendered). Code-only app: no XAML files.
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Spike.WinUiLatency;

public partial class SpikeApp : Application
{
    Window? _win;
    readonly List<double> _samples = new();
    readonly Stopwatch _sw = new();
    DispatcherQueueTimer? _timer;
    int _iteration = -1;
    double _constructMs, _coldShow;

    public SpikeApp()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var sw = Stopwatch.StartNew();
        var list = new ListView { Margin = new Thickness(0, 8, 0, 0) };
        for (int i = 1; i <= 8; i++) list.Items.Add($"Resultado {i}  —  C:\\Programas\\App{i}.exe");
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBox { FontSize = 20, PlaceholderText = "Buscar..." });
        panel.Children.Add(list);

        _win = new Window { Content = panel, SystemBackdrop = new MicaBackdrop() };
        var presenter = (OverlappedPresenter)_win.AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        _win.AppWindow.IsShownInSwitchers = false;
        _win.AppWindow.MoveAndResize(new RectInt32(400, 250, 640, 420));
        _constructMs = sw.Elapsed.TotalMilliseconds;

        CompositionTarget.Rendered += OnRendered;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(120);
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => Next();
        Next();
    }

    void Next()
    {
        _iteration++;
        if (_iteration > 15)
        {
            CompositionTarget.Rendered -= OnRendered;
            var warm = _samples.Skip(1).OrderBy(x => x).ToList();
            Console.WriteLine($"WinUI 3 (WindowsAppSDK self-contained, .NET {Environment.Version})");
            Console.WriteLine($"  construct window:        {_constructMs:F1} ms");
            Console.WriteLine($"  first Show -> frame:     {_coldShow:F1} ms (cold)");
            Console.WriteLine($"  warm Show -> frame (n={warm.Count}): min {warm.First():F1} | median {warm[warm.Count / 2]:F1} | max {warm.Last():F1} ms");
            _win!.Close();
            Exit();
            return;
        }
        _sw.Restart();
        _win!.AppWindow.Show();
        _win.Activate();
    }

    void OnRendered(object? sender, object e)
    {
        if (!_sw.IsRunning) return;
        _sw.Stop();
        double ms = _sw.Elapsed.TotalMilliseconds;
        _samples.Add(ms);
        if (_iteration == 0) _coldShow = ms;
        _win!.AppWindow.Hide();
        _timer!.Start();
    }
}
