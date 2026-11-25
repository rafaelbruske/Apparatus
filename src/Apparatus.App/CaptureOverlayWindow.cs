using System.Runtime.InteropServices.WindowsRuntime;
using Apparatus.Core;
using Apparatus.Core.Capture;
using Apparatus.Native;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using WinRT.Interop;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Apparatus.App;

public enum AnnotationTool { None, Pen, Line, Arrow, Rectangle, Marker, Text }

/// <summary>A Grid that can show a cursor (the protected property is not reachable from outside).</summary>
internal sealed class CursorGrid : Grid
{
    private InputSystemCursorShape? _shape;

    public void SetCursor(InputSystemCursorShape shape)
    {
        if (_shape == shape) return;
        _shape = shape;
        ProtectedCursor = InputSystemCursor.Create(shape);
    }
}

/// <summary>
/// One frozen monitor covered by a full-screen window: the user drags a rectangle, adjusts it, draws on it and then copies, saves
/// or prints it. The picture shown is the one grabbed before the window appeared, so nothing moves while the user decides.
/// </summary>
public sealed class CaptureOverlayWindow : Window
{
    private const double GrabDip = 7, HandleDip = 9, StrokeDip = 3, MarkerDip = 18, BarGap = 8;

    private static readonly string[] Palette =
    {
        "#FF0000", "#FF8C00", "#FFD400", "#22B14C", "#00B7EB", "#1E5BFF", "#8E44AD", "#000000", "#FFFFFF", "#808080",
    };

    private readonly CaptureSession _session;
    private readonly MonitorInfoData _monitor;
    private readonly nint _hwnd;
    private readonly RectD _bounds;               // the whole window, in DIPs
    private readonly double _scale;               // physical pixels per DIP
    private readonly int _pixelWidth, _pixelHeight;

    private readonly CursorGrid _root = new() { Background = new SolidColorBrush(Colors.Black), IsTabStop = true };
    private readonly Grid _stage = new();
    private readonly Canvas _ink = new();
    private readonly Canvas _chrome = new();
    private readonly Path _dim = new() { Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x80, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _frameLight = new() { Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 1, IsHitTestVisible = false };
    private readonly Rectangle _frameDark = new()
    {
        Stroke = new SolidColorBrush(Colors.Black), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 }, IsHitTestVisible = false,
    };
    private readonly Dictionary<SelectionHandle, Rectangle> _handles = new();
    private readonly Border _sizeLabel = new();
    private readonly TextBlock _sizeText = new() { Foreground = new SolidColorBrush(Colors.White), FontSize = 12 };
    private readonly Border _toolbar = new();
    private readonly Dictionary<AnnotationTool, ToggleButton> _toolButtons = new();
    private readonly Rectangle _colorSwatch = new() { Width = 18, Height = 18, RadiusX = 3, RadiusY = 3, Stroke = new SolidColorBrush(Colors.Gray), StrokeThickness = 1 };
    private readonly Stack<UIElement> _history = new();
    private readonly Border _tip = new();
    private readonly TextBlock _tipText = new() { Foreground = new SolidColorBrush(Colors.White), FontSize = 12 };
    private readonly Dictionary<FrameworkElement, string> _tips = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tipTimer;
    private FrameworkElement? _tipTarget;

    /// <summary>Wait before the hint of a toolbar button appears. The standard XAML tooltip takes about a second, which feels like it never shows.</summary>
    private const int TipDelayMs = 250;
    private readonly Rectangle _textColorSwatch = new() { Width = 18, Height = 18, RadiusX = 3, RadiusY = 3, Stroke = new SolidColorBrush(Colors.Gray), StrokeThickness = 1 };
    private StackPanel _mainBar = null!, _textBar = null!;
    private ToggleButton _boldButton = null!, _italicButton = null!, _underlineButton = null!;
    private ComboBox _sizeBox = null!, _fontBox = null!;
    private string _fontName = "Segoe UI";
    private double _fontSize = 20;
    private bool _bold, _italic, _underline, _syncingTextBar;

    private static readonly string[] Fonts =
    {
        "Segoe UI", "Arial", "Calibri", "Verdana", "Georgia", "Times New Roman", "Courier New", "Consolas", "Comic Sans MS", "Impact",
    };

    private static readonly double[] FontSizes = { 12, 14, 16, 18, 20, 24, 28, 32, 40, 48, 64, 72 };

    private RectD _selection;                      // empty when there is none
    private AnnotationTool _tool = AnnotationTool.None;
    private Windows.UI.Color _color;
    private bool _pointerDown;
    private SelectionHandle _dragHandle;
    private Point _dragStart;
    private RectD _selectionAtDragStart;
    private bool _selecting;
    private Point _anchor;
    private UIElement? _drawing;
    private Point _drawStart;
    private TextBox? _textBox;
    private bool _closing;
    private bool _busy;

    public CaptureOverlayWindow(CaptureSession session, MonitorInfoData monitor, CapturedFrame frame, string colorHex)
    {
        _session = session;
        _monitor = monitor;
        _scale = monitor.Scale;
        _pixelWidth = frame.Width;
        _pixelHeight = frame.Height;
        _bounds = new RectD(0, 0, frame.Width / _scale, frame.Height / _scale);
        _color = ParseColor(colorHex);

        BuildContent(frame);
        Content = _root;

        _hwnd = WindowNative.GetWindowHandle(this);
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.IsShownInSwitchers = false;
        WindowHelpers.UseRoundedCorners(_hwnd, rounded: false);

        AppWindow.Closing += (_, _) => _closing = true;
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated && !_closing) _root.Focus(FocusState.Programmatic);
        };
    }

    public MonitorInfoData Monitor => _monitor;

    /// <summary>Places the window exactly over its monitor and shows it.</summary>
    public void ShowOverMonitor(bool activate)
    {
        var b = _monitor.Bounds;
        AppWindow.Move(new PointInt32((int)b.X, (int)b.Y));   // pick up that monitor's DPI before sizing
        AppWindow.MoveAndResize(new RectInt32((int)b.X, (int)b.Y, (int)b.Width, (int)b.Height));
        AppWindow.Show(activateWindow: activate);
        if (activate)
        {
            Activate();
            WindowHelpers.BringToForeground(_hwnd);
        }
    }

    public void FocusMe()
    {
        WindowHelpers.BringToForeground(_hwnd);
        _root.Focus(FocusState.Programmatic);
    }

    /// <summary>Another monitor's window started a selection: this one drops its own.</summary>
    public void ClearSelection()
    {
        CommitText();
        _selection = default;
        UpdateChrome();
    }

    // ------------------------------------------------------------ building

    private void BuildContent(CapturedFrame frame)
    {
        var bitmap = new WriteableBitmap(frame.Width, frame.Height);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(frame.Bgra, 0, frame.Bgra.Length);

        _stage.Width = _bounds.Width;
        _stage.Height = _bounds.Height;
        _stage.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Fill, IsHitTestVisible = false });
        _ink.Width = _bounds.Width;
        _ink.Height = _bounds.Height;
        _stage.Children.Add(_ink);

        _chrome.Width = _bounds.Width;
        _chrome.Height = _bounds.Height;
        _chrome.Children.Add(_dim);
        _chrome.Children.Add(_frameLight);
        _chrome.Children.Add(_frameDark);
        foreach (var h in new[]
        {
            SelectionHandle.TopLeft, SelectionHandle.Top, SelectionHandle.TopRight, SelectionHandle.Right,
            SelectionHandle.BottomRight, SelectionHandle.Bottom, SelectionHandle.BottomLeft, SelectionHandle.Left,
        })
        {
            var r = new Rectangle
            {
                Width = HandleDip, Height = HandleDip, Fill = new SolidColorBrush(Colors.White),
                Stroke = new SolidColorBrush(Colors.Black), StrokeThickness = 1, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            };
            _handles[h] = r;
            _chrome.Children.Add(r);
        }

        _sizeLabel.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xD8, 0x20, 0x20, 0x20));
        _sizeLabel.CornerRadius = new CornerRadius(4);
        _sizeLabel.Padding = new Thickness(8, 3, 8, 3);
        _sizeLabel.Child = _sizeText;
        _sizeLabel.Visibility = Visibility.Collapsed;
        _sizeLabel.IsHitTestVisible = false;
        _chrome.Children.Add(_sizeLabel);

        BuildToolbar();
        _toolbar.Visibility = Visibility.Collapsed;
        _chrome.Children.Add(_toolbar);   // the only part of the chrome that takes the pointer

        _tip.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xF2, 0x2B, 0x2B, 0x2B));
        _tip.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x55, 0x55, 0x55));
        _tip.BorderThickness = new Thickness(1);
        _tip.CornerRadius = new CornerRadius(4);
        _tip.Padding = new Thickness(8, 4, 8, 4);
        _tip.Child = _tipText;
        _tip.Visibility = Visibility.Collapsed;
        _tip.IsHitTestVisible = false;
        _chrome.Children.Add(_tip);

        _root.Children.Add(_stage);
        _root.Children.Add(_chrome);
        _root.PointerPressed += OnPointerPressed;
        _root.PointerMoved += OnPointerMoved;
        _root.PointerReleased += OnPointerReleased;
        _root.PointerCaptureLost += (_, _) => _pointerDown = false;
        _root.KeyDown += OnKeyDown;
        UpdateChrome();
    }

    private void BuildToolbar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };

        AddTool(bar, AnnotationTool.Pen, IconPath("M2,15 C5,4 8,19 11,9 S16,7 18,5", filled: false), "Lápis");
        AddTool(bar, AnnotationTool.Line, IconPath("M3,17 L17,3", filled: false), "Linha");
        AddTool(bar, AnnotationTool.Arrow, IconPath("M3,17 L17,3 M8,3 L17,3 L17,12", filled: false), "Seta");
        AddTool(bar, AnnotationTool.Rectangle, IconPath("M3,4 L17,4 L17,16 L3,16 Z", filled: false), "Retângulo");
        AddTool(bar, AnnotationTool.Marker, new FontIcon { Glyph = "", FontSize = 16 }, "Marca-texto");
        AddTool(bar, AnnotationTool.Text, new FontIcon { Glyph = "", FontSize = 16 }, "Texto");

        _colorSwatch.Fill = new SolidColorBrush(_color);
        var colorButton = new Button
        {
            Content = _colorSwatch, Padding = new Thickness(8), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
            Flyout = BuildColorFlyout(),
        };
        SetTip(colorButton, "Cor");
        bar.Children.Add(colorButton);

        bar.Children.Add(Separator());
        bar.Children.Add(PlainButton("", "Imprimir (Ctrl+P)", async (_, _) => await RunAsync(PrintAsync)));
        bar.Children.Add(PlainButton("", "Copiar (Ctrl+C)", async (_, _) => await RunAsync(CopyAsync)));
        bar.Children.Add(PlainButton("", "Salvar (Ctrl+S)", async (_, _) => await RunAsync(SaveAsync)));
        bar.Children.Add(PlainButton("", "Desfazer (Ctrl+Z)", (_, _) => Undo()));
        bar.Children.Add(PlainButton("", "Fechar (Esc)", (_, _) => _session.Close()));

        _toolbar.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xF2, 0x2B, 0x2B, 0x2B));
        _toolbar.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x55, 0x55, 0x55));
        _toolbar.BorderThickness = new Thickness(1);
        _toolbar.CornerRadius = new CornerRadius(8);
        _toolbar.Padding = new Thickness(4);
        _toolbar.RequestedTheme = ElementTheme.Dark;
        _mainBar = bar;
        _textBar = BuildTextBar();
        _toolbar.Child = _mainBar;
    }

    /// <summary>The bar shown while the text tool is active: back, then the font controls.</summary>
    private StackPanel BuildTextBar()
    {
        _textColorSwatch.Fill = new SolidColorBrush(_color);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };

        bar.Children.Add(PlainButton("\uE72B", "Voltar às ferramentas", (_, _) => SelectTool(AnnotationTool.None)));
        bar.Children.Add(PlainButton("\uE7A7", "Desfazer (Ctrl+Z)", (_, _) => Undo()));
        bar.Children.Add(Separator());

        _boldButton = StyleToggle("B", "Negrito (Ctrl+B)", Microsoft.UI.Text.FontWeights.Bold, Windows.UI.Text.FontStyle.Normal, false, on => _bold = on);
        _italicButton = StyleToggle("I", "Itálico (Ctrl+I)", Microsoft.UI.Text.FontWeights.Normal, Windows.UI.Text.FontStyle.Italic, false, on => _italic = on);
        _underlineButton = StyleToggle("U", "Sublinhado (Ctrl+U)", Microsoft.UI.Text.FontWeights.Normal, Windows.UI.Text.FontStyle.Normal, true, on => _underline = on);
        bar.Children.Add(_boldButton);
        bar.Children.Add(_italicButton);
        bar.Children.Add(_underlineButton);

        var colorButton = new Button
        {
            Content = _textColorSwatch, Padding = new Thickness(8), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
            Flyout = BuildColorFlyout(),
        };
        SetTip(colorButton, "Cor do texto");
        bar.Children.Add(colorButton);
        bar.Children.Add(Separator());

        _sizeBox = new ComboBox { Width = 76, Height = 36, VerticalAlignment = VerticalAlignment.Center };
        foreach (double size in FontSizes) _sizeBox.Items.Add(new ComboBoxItem { Content = size.ToString("0"), Tag = size });
        _sizeBox.SelectedIndex = Array.IndexOf(FontSizes, _fontSize);
        SetTip(_sizeBox, "Tamanho da fonte");
        _sizeBox.SelectionChanged += (_, _) =>
        {
            if (_syncingTextBar || _sizeBox.SelectedItem is not ComboBoxItem { Tag: double size }) return;
            _fontSize = size;
            ApplyTextStyle();
        };
        bar.Children.Add(_sizeBox);

        _fontBox = new ComboBox { Width = 160, Height = 36, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) };
        foreach (string font in Fonts) _fontBox.Items.Add(new ComboBoxItem { Content = font, Tag = font, FontFamily = new FontFamily(font) });
        _fontBox.SelectedIndex = Array.IndexOf(Fonts, _fontName);
        SetTip(_fontBox, "Fonte");
        _fontBox.SelectionChanged += (_, _) =>
        {
            if (_syncingTextBar || _fontBox.SelectedItem is not ComboBoxItem { Tag: string font }) return;
            _fontName = font;
            ApplyTextStyle();
        };
        bar.Children.Add(_fontBox);
        bar.Children.Add(new TextBlock
        {
            Text = "Ctrl + Enter finaliza a edição", FontSize = 12, Opacity = 0.75, Foreground = new SolidColorBrush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0),
        });
        return bar;
    }

    private ToggleButton StyleToggle(string letter, string tip, Windows.UI.Text.FontWeight weight, Windows.UI.Text.FontStyle style, bool underline, Action<bool> set)
    {
        var label = new TextBlock { Text = letter, FontSize = 16, FontWeight = weight, FontStyle = style, FontFamily = new FontFamily("Segoe UI") };
        if (underline) label.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
        var button = new ToggleButton
        {
            Content = label, Width = 36, Height = 36, Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
        };
        SetTip(button, tip);
        button.Click += (_, _) =>
        {
            set(button.IsChecked == true);
            ApplyTextStyle();
        };
        return button;
    }

    /// <summary>Applies the font choices to the text being typed (new text picks them up as it is created).</summary>
    private void ApplyTextStyle()
    {
        if (_textBox is { } box) StyleBox(box);
        FocusAfterToolbar();
    }

    private void StyleBox(TextBox box)
    {
        box.FontFamily = new FontFamily(_fontName);
        box.FontSize = _fontSize;
        box.FontWeight = _bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        box.FontStyle = _italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
    }

    /// <summary>A click on the toolbar takes the keyboard; give it back to the text being typed, if any.</summary>
    private void FocusAfterToolbar()
    {
        if (_textBox is { } box) box.Focus(FocusState.Programmatic);
        else _root.Focus(FocusState.Programmatic);
    }

    private void ApplyColor(string hex)
    {
        _color = ParseColor(hex);
        _colorSwatch.Fill = new SolidColorBrush(_color);
        _textColorSwatch.Fill = new SolidColorBrush(_color);
        _session.RememberColor(hex);
        if (_textBox is { } box)
        {
            box.Foreground = ColorBrush();
            box.BorderBrush = ColorBrush();
        }
    }

    /// <summary>Gives a toolbar control a hint with its name and shortcut, shown a quarter of a second after the pointer rests on it.</summary>
    private void SetTip(FrameworkElement element, string text)
    {
        _tips[element] = text;
        element.PointerEntered += (_, _) => ScheduleTip(element);
        element.PointerExited += (_, _) => HideTip();
        // Buttons and combo boxes mark the press as handled, so ask to hear about it anyway.
        element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => HideTip()), handledEventsToo: true);
    }

    private void ScheduleTip(FrameworkElement element)
    {
        HideTip();
        _tipTarget = element;
        _tipTimer ??= CreateTipTimer();
        _tipTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateTipTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(TipDelayMs);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => ShowTip();
        return timer;
    }

    private void ShowTip()
    {
        if (_tipTarget is not { } element || !_tips.TryGetValue(element, out var text) || element.XamlRoot is null) return;

        _tipText.Text = text;
        _tip.Visibility = Visibility.Visible;   // a collapsed element measures as zero
        _tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _tip.DesiredSize;
        var origin = element.TransformToVisual(_chrome).TransformPoint(new Point(0, 0));

        double x = Math.Clamp(origin.X + element.ActualWidth / 2 - size.Width / 2, 4, Math.Max(4, _bounds.Width - size.Width - 4));
        double y = origin.Y - size.Height - 6;
        if (y < 4) y = origin.Y + element.ActualHeight + 6;   // no room above: below the button
        Canvas.SetLeft(_tip, x);
        Canvas.SetTop(_tip, y);
        _tip.Visibility = Visibility.Visible;
    }

    private void HideTip()
    {
        _tipTimer?.Stop();
        _tipTarget = null;
        _tip.Visibility = Visibility.Collapsed;
    }

    private static Path IconPath(string data, bool filled) => new()
    {
        Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data),
        Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        Fill = filled ? new SolidColorBrush(Colors.White) : null, Width = 20, Height = 20, Stretch = Stretch.None,
    };

    private void AddTool(StackPanel bar, AnnotationTool tool, UIElement icon, string tip)
    {
        var button = new ToggleButton
        {
            Content = icon, Width = 36, Height = 36, Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        SetTip(button, tip);
        button.Click += (_, _) => SelectTool(button.IsChecked == true ? tool : AnnotationTool.None);
        _toolButtons[tool] = button;
        bar.Children.Add(button);
    }

    private Button PlainButton(string glyph, string tip, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 16 }, Width = 36, Height = 36, Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
        };
        SetTip(button, tip);
        button.Click += click;
        return button;
    }

    private static Border Separator() => new()
    {
        Width = 1, Margin = new Thickness(6, 6, 6, 6), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x66, 0x66, 0x66)),
    };

    private Flyout BuildColorFlyout()
    {
        var grid = new Grid { RowSpacing = 6, ColumnSpacing = 6 };
        for (int i = 0; i < 5; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < 2; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var flyout = new Flyout { Content = grid };
        for (int i = 0; i < Palette.Length; i++)
        {
            string hex = Palette[i];
            var swatch = new Button
            {
                Width = 30, Height = 30, Padding = new Thickness(0), CornerRadius = new CornerRadius(15),
                Background = new SolidColorBrush(ParseColor(hex)), BorderBrush = new SolidColorBrush(Colors.Gray), BorderThickness = new Thickness(1),
            };
            ToolTipService.SetToolTip(swatch, hex);
            swatch.Click += (_, _) =>
            {
                ApplyColor(hex);
                flyout.Hide();
                FocusAfterToolbar();
            };
            Grid.SetRow(swatch, i / 5);
            Grid.SetColumn(swatch, i % 5);
            grid.Children.Add(swatch);
        }
        return flyout;
    }

    private static Windows.UI.Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int v))
            return Windows.UI.Color.FromArgb(0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return Colors.Red;
    }

    // ------------------------------------------------------------ chrome

    private void UpdateChrome()
    {
        bool has = !_selection.IsEmpty;
        var full = new RectangleGeometry { Rect = new Rect(0, 0, _bounds.Width, _bounds.Height) };
        if (has)
        {
            var hole = new RectangleGeometry { Rect = new Rect(_selection.X, _selection.Y, _selection.Width, _selection.Height) };
            _dim.Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { full, hole } };
        }
        else
        {
            _dim.Data = full;
        }

        foreach (var shape in new[] { _frameLight, _frameDark })
        {
            shape.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (!has) continue;
            Canvas.SetLeft(shape, _selection.X - 0.5);
            Canvas.SetTop(shape, _selection.Y - 0.5);
            shape.Width = _selection.Width + 1;
            shape.Height = _selection.Height + 1;
        }

        _ink.Clip = has ? new RectangleGeometry { Rect = new Rect(_selection.X, _selection.Y, _selection.Width, _selection.Height) } : null;

        foreach (var (handle, rect) in _handles)
        {
            rect.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (!has) continue;
            double x = handle switch
            {
                SelectionHandle.TopLeft or SelectionHandle.Left or SelectionHandle.BottomLeft => _selection.X,
                SelectionHandle.Top or SelectionHandle.Bottom => _selection.X + _selection.Width / 2,
                _ => _selection.Right,
            };
            double y = handle switch
            {
                SelectionHandle.TopLeft or SelectionHandle.Top or SelectionHandle.TopRight => _selection.Y,
                SelectionHandle.Left or SelectionHandle.Right => _selection.Y + _selection.Height / 2,
                _ => _selection.Bottom,
            };
            Canvas.SetLeft(rect, x - HandleDip / 2);
            Canvas.SetTop(rect, y - HandleDip / 2);
        }

        _sizeLabel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (has)
        {
            _sizeText.Text = $"{(int)Math.Round(_selection.Width * _scale)} × {(int)Math.Round(_selection.Height * _scale)}";
            _sizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double ly = _selection.Y - _sizeLabel.DesiredSize.Height - 6;
            if (ly < 2) ly = _selection.Y + 6;
            Canvas.SetLeft(_sizeLabel, Math.Max(2, _selection.X));
            Canvas.SetTop(_sizeLabel, ly);
        }

        bool showBar = has && !_selecting;
        _toolbar.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        if (showBar)
        {
            _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var (bx, by) = SelectionGeometry.ToolbarPosition(_selection, _toolbar.DesiredSize.Width, _toolbar.DesiredSize.Height, _bounds, BarGap);
            Canvas.SetLeft(_toolbar, bx);
            Canvas.SetTop(_toolbar, by);
        }
    }

    // ------------------------------------------------------------ pointer

    private bool IsOverToolbar(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null)
        {
            if (ReferenceEquals(node, _toolbar)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_busy || IsOverToolbar(e.OriginalSource)) return;
        var point = e.GetCurrentPoint(_root);
        if (!point.Properties.IsLeftButtonPressed) return;
        var p = point.Position;

        // A click outside a text being typed finishes it first.
        if (_textBox is not null && !ReferenceEquals(e.OriginalSource, _textBox)) CommitText();

        _root.CapturePointer(e.Pointer);
        _pointerDown = true;
        _dragStart = p;
        _selectionAtDragStart = _selection;

        var handle = SelectionGeometry.HitTest(_selection, p.X, p.Y, GrabDip);
        if (handle is not (SelectionHandle.None or SelectionHandle.Inside))
        {
            _dragHandle = handle;   // resizing works with any tool
            return;
        }

        if (handle == SelectionHandle.Inside && _tool != AnnotationTool.None)
        {
            BeginDraw(p);
            return;
        }

        if (handle == SelectionHandle.Inside)
        {
            _dragHandle = SelectionHandle.Inside;   // move
            return;
        }

        // Outside: a new selection (the other monitors drop theirs).
        _session.SelectionStartedOn(this);
        _selecting = true;
        _dragHandle = SelectionHandle.None;
        _anchor = p;
        _selection = default;
        UpdateChrome();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_root).Position;
        if (!_pointerDown)
        {
            UpdateCursor(p);
            return;
        }

        if (_drawing is not null)
        {
            ContinueDraw(p);
            return;
        }

        if (_selecting)
        {
            _selection = RectD.FromCorners(_anchor.X, _anchor.Y, p.X, p.Y).ClampTo(_bounds);
            UpdateChrome();
            return;
        }

        if (_dragHandle != SelectionHandle.None)
        {
            _selection = SelectionGeometry.Drag(_selectionAtDragStart, _dragHandle, p.X - _dragStart.X, p.Y - _dragStart.Y, _bounds);
            UpdateChrome();
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown) return;
        OnPointerMoved(sender, e);   // the last position counts even if no move event came before the release
        _pointerDown = false;
        _root.ReleasePointerCapture(e.Pointer);

        if (_drawing is not null)
        {
            EndDraw();
            return;
        }

        if (_selecting)
        {
            _selecting = false;
            if (_selection.Width < 4 || _selection.Height < 4) _selection = default;   // a click, not a drag
        }
        _dragHandle = SelectionHandle.None;
        UpdateChrome();
        UpdateCursor(e.GetCurrentPoint(_root).Position);
    }

    private void UpdateCursor(Point p)
    {
        var handle = SelectionGeometry.HitTest(_selection, p.X, p.Y, GrabDip);
        _root.SetCursor(handle switch
        {
            SelectionHandle.TopLeft or SelectionHandle.BottomRight => InputSystemCursorShape.SizeNorthwestSoutheast,
            SelectionHandle.TopRight or SelectionHandle.BottomLeft => InputSystemCursorShape.SizeNortheastSouthwest,
            SelectionHandle.Top or SelectionHandle.Bottom => InputSystemCursorShape.SizeNorthSouth,
            SelectionHandle.Left or SelectionHandle.Right => InputSystemCursorShape.SizeWestEast,
            SelectionHandle.Inside => _tool == AnnotationTool.Text ? InputSystemCursorShape.IBeam
                : _tool != AnnotationTool.None ? InputSystemCursorShape.Cross : InputSystemCursorShape.SizeAll,
            _ => InputSystemCursorShape.Cross,
        });
    }

    // ----------------------------------------------------------- drawing

    private void SelectTool(AnnotationTool tool)
    {
        CommitText();
        _tool = tool;
        foreach (var (t, button) in _toolButtons) button.IsChecked = t == tool;
        // The text tool has its own bar (back + font controls); every other tool uses the usual one.
        HideTip();
        _toolbar.Child = tool == AnnotationTool.Text ? _textBar : _mainBar;
        UpdateChrome();
    }

    private SolidColorBrush ColorBrush(byte alpha = 0xFF) => new(Windows.UI.Color.FromArgb(alpha, _color.R, _color.G, _color.B));

    private void BeginDraw(Point p)
    {
        _drawStart = Clamp(p);
        switch (_tool)
        {
            case AnnotationTool.Pen:
                var pen = new Polyline
                {
                    Stroke = ColorBrush(), StrokeThickness = StrokeDip, StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
                pen.Points.Add(_drawStart);
                _drawing = pen;
                break;
            case AnnotationTool.Line:
                _drawing = new Line
                {
                    Stroke = ColorBrush(), StrokeThickness = StrokeDip, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    X1 = _drawStart.X, Y1 = _drawStart.Y, X2 = _drawStart.X, Y2 = _drawStart.Y,
                };
                break;
            case AnnotationTool.Marker:
                _drawing = new Line
                {
                    Stroke = ColorBrush(0x66), StrokeThickness = MarkerDip, StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat,
                    X1 = _drawStart.X, Y1 = _drawStart.Y, X2 = _drawStart.X, Y2 = _drawStart.Y,
                };
                break;
            case AnnotationTool.Arrow:
                _drawing = new Path
                {
                    Stroke = ColorBrush(), StrokeThickness = StrokeDip, StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Data = ArrowData(_drawStart, _drawStart),
                };
                break;
            case AnnotationTool.Rectangle:
                var rect = new Rectangle { Stroke = ColorBrush(), StrokeThickness = StrokeDip, Width = 0, Height = 0 };
                Canvas.SetLeft(rect, _drawStart.X);
                Canvas.SetTop(rect, _drawStart.Y);
                _drawing = rect;
                break;
            case AnnotationTool.Text:
                _pointerDown = false;
                _root.ReleasePointerCaptures();   // the text box must be able to take the keyboard
                BeginText(_drawStart);
                return;
        }
        if (_drawing is not null) _ink.Children.Add(_drawing);
    }

    private void ContinueDraw(Point raw)
    {
        var p = Clamp(raw);
        switch (_drawing)
        {
            case Polyline pen: pen.Points.Add(p); break;
            case Line line: line.X2 = p.X; line.Y2 = p.Y; break;
            case Path arrow: arrow.Data = ArrowData(_drawStart, p); break;
            case Rectangle rect:
                var r = RectD.FromCorners(_drawStart.X, _drawStart.Y, p.X, p.Y);
                Canvas.SetLeft(rect, r.X);
                Canvas.SetTop(rect, r.Y);
                rect.Width = r.Width;
                rect.Height = r.Height;
                break;
        }
    }

    private void EndDraw()
    {
        var shape = _drawing;
        _drawing = null;
        if (shape is null) return;

        // A click without a drag leaves nothing worth keeping.
        bool empty = shape switch
        {
            Polyline pen => pen.Points.Count < 2,
            Line line => Math.Abs(line.X2 - line.X1) < 1 && Math.Abs(line.Y2 - line.Y1) < 1,
            Rectangle rect => rect.Width < 2 && rect.Height < 2,
            _ => false,
        };
        if (empty) _ink.Children.Remove(shape);
        else _history.Push(shape);
    }

    private static Geometry ArrowData(Point from, Point to)
    {
        var group = new GeometryGroup();
        group.Children.Add(new LineGeometry { StartPoint = from, EndPoint = to });
        var (l, r) = ArrowGeometry.Barbs(from.X, from.Y, to.X, to.Y, StrokeDip);
        group.Children.Add(new LineGeometry { StartPoint = to, EndPoint = new Point(l.X, l.Y) });
        group.Children.Add(new LineGeometry { StartPoint = to, EndPoint = new Point(r.X, r.Y) });
        return group;
    }

    private Point Clamp(Point p) => new(
        Math.Clamp(p.X, _selection.X, _selection.Right),
        Math.Clamp(p.Y, _selection.Y, _selection.Bottom));

    private void BeginText(Point at)
    {
        var box = new TextBox
        {
            MinWidth = 80, Padding = new Thickness(2), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            Foreground = ColorBrush(),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0, 0, 0)),
            BorderBrush = ColorBrush(), BorderThickness = new Thickness(1),
        };
        StyleBox(box);
        Canvas.SetLeft(box, at.X);
        Canvas.SetTop(box, at.Y);
        // Enter is a line break now, so finishing needs Ctrl+Enter. The preview event comes before the box handles the key itself.
        box.PreviewKeyDown += (_, e) =>
        {
            bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (e.Key == VirtualKey.Enter && ctrl) { CommitText(); e.Handled = true; }
            else if (e.Key == VirtualKey.Escape) { CancelText(); e.Handled = true; }
        };
        _textBox = box;
        _ink.Children.Add(box);
        // Pressing the mouse gives the focus to the window once the handler has returned, so the box asks for it a moment later.
        var focus = DispatcherQueue.CreateTimer();
        focus.Interval = TimeSpan.FromMilliseconds(120);
        focus.IsRepeating = false;
        focus.Tick += (_, _) => box.Focus(FocusState.Programmatic);
        focus.Start();
    }

    private void CommitText()
    {
        var box = _textBox;
        if (box is null) return;
        _textBox = null;
        _ink.Children.Remove(box);
        if (string.IsNullOrWhiteSpace(box.Text)) return;

        var label = new TextBlock
        {
            Text = box.Text, FontFamily = box.FontFamily, FontSize = box.FontSize, FontWeight = box.FontWeight, FontStyle = box.FontStyle,
            Foreground = box.Foreground,
            TextDecorations = _underline ? Windows.UI.Text.TextDecorations.Underline : Windows.UI.Text.TextDecorations.None,
        };
        Canvas.SetLeft(label, Canvas.GetLeft(box) + 3);
        Canvas.SetTop(label, Canvas.GetTop(box) + 3);
        _ink.Children.Add(label);
        _history.Push(label);
        _root.Focus(FocusState.Programmatic);
    }

    private void CancelText()
    {
        if (_textBox is null) return;
        _ink.Children.Remove(_textBox);
        _textBox = null;
        _root.Focus(FocusState.Programmatic);
    }

    /// <summary>Takes back the last thing: the text still being typed, or else the last drawing or text that was inserted.</summary>
    private void Undo()
    {
        if (_textBox is not null)
        {
            CancelText();
            return;
        }
        if (_history.Count > 0) _ink.Children.Remove(_history.Pop());
    }

    // --------------------------------------------------------------- keys

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && _textBox is not null && (e.Key == VirtualKey.B || e.Key == VirtualKey.I || e.Key == VirtualKey.U))
        {
            e.Handled = true;
            var button = e.Key == VirtualKey.B ? _boldButton : e.Key == VirtualKey.I ? _italicButton : _underlineButton;
            button.IsChecked = button.IsChecked != true;
            if (e.Key == VirtualKey.B) _bold = button.IsChecked == true;
            else if (e.Key == VirtualKey.I) _italic = button.IsChecked == true;
            else _underline = button.IsChecked == true;
            ApplyTextStyle();
            return;
        }
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            if (_textBox is not null) CancelText();
            else _session.Close();
        }
        else if (ctrl && e.Key == VirtualKey.Z) { e.Handled = true; Undo(); }
        else if (ctrl && e.Key == VirtualKey.C && _textBox is null) { e.Handled = true; await RunAsync(CopyAsync); }
        else if (ctrl && e.Key == VirtualKey.S) { e.Handled = true; await RunAsync(SaveAsync); }
        else if (ctrl && e.Key == VirtualKey.P) { e.Handled = true; await RunAsync(PrintAsync); }
    }

    // ------------------------------------------------------------ results

    private sealed record Rendered(byte[] Bgra, int Width, int Height);

    /// <summary>Runs one of the end actions; it ignores a second request while the first one is still working.</summary>
    private async Task RunAsync(Func<Task<bool>> action)
    {
        if (_busy || _selection.IsEmpty) return;
        _busy = true;
        try
        {
            if (await action()) _session.Close();
        }
        catch (Exception ex)
        {
            AppLog.Error("Screen capture action failed", ex);
            _session.Services.Host.ShowBalloon("Captura de tela", "Não foi possível concluir a captura: " + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>The selected part of the picture with the drawings on it, at the screen's own resolution.</summary>
    private async Task<Rendered> RenderSelectionAsync()
    {
        CommitText();
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(_stage, _pixelWidth, _pixelHeight);
        byte[] all = (await bitmap.GetPixelsAsync()).ToArray();

        int x = Math.Clamp((int)Math.Round(_selection.X * _scale), 0, _pixelWidth - 1);
        int y = Math.Clamp((int)Math.Round(_selection.Y * _scale), 0, _pixelHeight - 1);
        int w = Math.Clamp((int)Math.Round(_selection.Width * _scale), 1, _pixelWidth - x);
        int h = Math.Clamp((int)Math.Round(_selection.Height * _scale), 1, _pixelHeight - y);

        var cropped = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
            System.Buffer.BlockCopy(all, ((y + row) * _pixelWidth + x) * 4, cropped, row * w * 4, w * 4);
        return new Rendered(cropped, w, h);
    }

    private static Task<byte[]> EncodeAsync(Rendered image, CaptureImageFormat format, int quality = CaptureImageFormats.DefaultQuality) =>
        CaptureImage.EncodeAsync(image.Bgra, image.Width, image.Height, format, quality);

    private async Task<bool> CopyAsync()
    {
        var png = await EncodeAsync(await RenderSelectionAsync(), CaptureImageFormat.Png);
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        return true;
    }

    /// <summary>Shows "saved" only when the user asked for notifications (off by default).</summary>
    private void NotifySaved(string path)
    {
        if (_session.Services.Settings.Current.CaptureNotifyOnSave)
            _session.Services.Host.ShowBalloon("Captura salva", path);
    }

    private async Task<bool> SaveAsync()
    {
        var image = await RenderSelectionAsync();

        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(CaptureNaming.FileName(DateTime.Now)),
        };
        // The format chosen in the settings is the one the dialog offers first (and so saves by default).
        var settings = _session.Services.Settings.Current;
        var png = ("Imagem PNG", new List<string> { ".png" });
        var jpeg = ("Imagem JPEG", new List<string> { ".jpg", ".jpeg" });
        foreach (var (label, extensions) in CaptureImageFormats.Parse(settings.CaptureFormat) == CaptureImageFormat.Jpeg ? new[] { jpeg, png } : new[] { png, jpeg })
            picker.FileTypeChoices.Add(label, extensions);
        InitializeWithWindow.Initialize(picker, _hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return false;   // cancelled: stay in the editor

        var bytes = await EncodeAsync(image, CaptureImageFormats.FromExtension(file.FileType), settings.CaptureQuality);
        await File.WriteAllBytesAsync(file.Path, bytes);
        NotifySaved(file.Path);
        return true;
    }

    private async Task<bool> PrintAsync()
    {
        var png = await EncodeAsync(await RenderSelectionAsync(), CaptureImageFormat.Png);
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Apparatus");
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, CaptureNaming.FileName(DateTime.Now));
        await File.WriteAllBytesAsync(path, png);

        // The picture's own "print" verb opens Windows' photo printing dialog.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { Verb = "print", UseShellExecute = true })?.Dispose();
        return true;
    }
}
