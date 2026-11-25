using Apparatus.Core.Snippets;
using Apparatus.Native;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace Apparatus.App;

/// <summary>
/// Small always-on-top form that asks for a snippet's {field:...} and {choice:...} values.
/// Enter confirms, Esc cancels; closing the window cancels too.
/// </summary>
public sealed class SnippetFillWindow : Window
{
    private const double WidthDip = 460;

    private readonly IReadOnlyList<SnippetField> _fields;
    private readonly List<(SnippetField Field, Control Input)> _inputs = new();
    private readonly TaskCompletionSource<Dictionary<string, string>?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _finished;

    public SnippetFillWindow(string title, IReadOnlyList<SnippetField> fields)
    {
        _fields = fields;
        Title = title;
        SystemBackdrop = new DesktopAcrylicBackdrop();

        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });

        foreach (var field in fields)
        {
            Control input;
            if (field.IsChoice)
            {
                var combo = new ComboBox { Header = field.Label, HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = field.Options };
                combo.SelectedItem = field.Default;
                if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;
                input = combo;
            }
            else
            {
                var box = new TextBox { Header = field.Label, Text = field.Default, AcceptsReturn = false };
                box.KeyDown += OnFieldKeyDown;
                input = box;
            }
            input.KeyDown += OnFieldKeyDown;
            _inputs.Add((field, input));
            panel.Children.Add(input);
        }

        var insert = new Button { Content = "Inserir", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        insert.Click += (_, _) => Finish(submit: true);
        var cancel = new Button { Content = "Cancelar" };
        cancel.Click += (_, _) => Finish(submit: false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(insert);
        panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Text = "Enter insere · Esc cancela", FontSize = 11, Opacity = 0.55 });
        Content = panel;

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.IsShownInSwitchers = false;
        WindowHelpers.UseRoundedCorners(WindowNative.GetWindowHandle(this));

        Closed += (_, _) =>
        {
            _finished = true;
            _result.TrySetResult(null);   // closed any other way: cancel
        };
    }

    /// <summary>Shows the form and completes with the values, or null if the user cancelled.</summary>
    public Task<Dictionary<string, string>?> AskAsync()
    {
        Place();
        Activate();
        WindowHelpers.BringToForeground(WindowNative.GetWindowHandle(this));
        DispatcherQueue.TryEnqueue(() => _inputs[0].Input.Focus(FocusState.Programmatic));
        return _result.Task;
    }

    private void Place()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var (cx, cy) = WindowHelpers.CursorPosition();
        var area = DisplayArea.GetFromPoint(new PointInt32(cx, cy), DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Move(new PointInt32(area.X + 40, area.Y + 40));
        double scale = WindowHelpers.GetScale(hwnd);

        double height = 52 + _fields.Count * 76 + 84;
        int w = (int)(WidthDip * scale), h = (int)(height * scale);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (int)(area.Height * 0.3), w, h));
    }

    private void OnFieldKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { Finish(submit: true); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape) { Finish(submit: false); e.Handled = true; }
    }

    private void Finish(bool submit)
    {
        if (_finished) return;
        _finished = true;

        Dictionary<string, string>? values = null;
        if (submit)
        {
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (field, input) in _inputs)
            {
                values[field.Label] = input switch
                {
                    TextBox t => t.Text,
                    ComboBox c => c.SelectedItem as string ?? field.Default,
                    _ => field.Default,
                };
            }
        }
        _result.TrySetResult(values);
        Close();
    }
}
