using Apparatus.Native;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace Apparatus.App;

/// <summary>A small always-on-top bar shown while the keyboard is being recorded: it says so, counts steps and stops the recording.</summary>
public sealed class RecordingBanner : Window
{
    private const double WidthDip = 470, HeightDip = 56;

    private readonly Func<int> _stepCount;
    private readonly TextBlock _counter = new() { FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

    public RecordingBanner(Func<int> stepCount, Action stop)
    {
        _stepCount = stepCount;
        SystemBackdrop = new DesktopAcrylicBackdrop();

        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 12, Height = 12, Fill = new SolidColorBrush(Colors.Red), VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = "Gravando o teclado", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        label.Children.Add(_counter);
        var button = new Button { Content = "Parar (Pause)", VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => stop();

        var grid = new Grid { Padding = new Thickness(18, 0, 14, 0), ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(dot);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);
        Content = grid;

        ExtendsContentIntoTitleBar = true;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.IsShownInSwitchers = false;
        WindowHelpers.UseRoundedCorners(WindowNative.GetWindowHandle(this));

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(300);
        _timer.Tick += (_, _) => Refresh();
        Closed += (_, _) => _timer.Stop();
    }

    public void ShowAtTop()
    {
        nint hwnd = WindowNative.GetWindowHandle(this);
        var area = DisplayArea.Primary.WorkArea;
        AppWindow.Move(new PointInt32(area.X + 40, area.Y + 40));   // pick up that monitor's DPI first
        double scale = WindowHelpers.GetScale(hwnd);
        int w = (int)(WidthDip * scale), h = (int)(HeightDip * scale);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (int)(16 * scale), w, h));
        AppWindow.ResizeClient(new SizeInt32(w, h));
        Refresh();
        AppWindow.Show(activateWindow: false);   // never take the focus from the program being recorded
        _timer.Start();
    }

    private void Refresh()
    {
        int n = _stepCount();
        _counter.Text = n == 0 ? "Nada gravado ainda" : n == 1 ? "1 passo" : $"{n} passos";
    }
}
