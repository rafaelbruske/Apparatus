using Apparatus.Core.Capture;
using Apparatus.Core.Input;
using Apparatus.Core.Start;

namespace Apparatus.Core.Tests;

public class CaptureTests
{
    private static readonly RectD Screen = new(0, 0, 1000, 600);

    [Fact]
    public void FromCorners_works_in_any_drag_direction()
    {
        Assert.Equal(new RectD(10, 20, 100, 50), RectD.FromCorners(110, 70, 10, 20));
    }

    [Fact]
    public void ClampTo_keeps_the_part_inside_and_is_empty_when_outside()
    {
        Assert.Equal(new RectD(900, 500, 100, 100), new RectD(900, 500, 300, 300).ClampTo(Screen));
        Assert.True(new RectD(1200, 10, 50, 50).ClampTo(Screen).IsEmpty);
    }

    [Theory]
    [InlineData(100, 100, SelectionHandle.TopLeft)]
    [InlineData(300, 100, SelectionHandle.TopRight)]
    [InlineData(300, 200, SelectionHandle.BottomRight)]
    [InlineData(100, 200, SelectionHandle.BottomLeft)]
    [InlineData(200, 100, SelectionHandle.Top)]
    [InlineData(200, 200, SelectionHandle.Bottom)]
    [InlineData(100, 150, SelectionHandle.Left)]
    [InlineData(300, 150, SelectionHandle.Right)]
    [InlineData(200, 150, SelectionHandle.Inside)]
    [InlineData(500, 500, SelectionHandle.None)]
    public void HitTest_finds_handles_and_inside(double x, double y, SelectionHandle expected)
    {
        var sel = new RectD(100, 100, 200, 100);
        Assert.Equal(expected, SelectionGeometry.HitTest(sel, x, y, grab: 6));
    }

    [Fact]
    public void Dragging_inside_moves_but_never_leaves_the_screen()
    {
        var start = new RectD(100, 100, 200, 100);
        Assert.Equal(new RectD(150, 130, 200, 100), SelectionGeometry.Drag(start, SelectionHandle.Inside, 50, 30, Screen));
        Assert.Equal(new RectD(800, 500, 200, 100), SelectionGeometry.Drag(start, SelectionHandle.Inside, 5000, 5000, Screen));
        Assert.Equal(new RectD(0, 0, 200, 100), SelectionGeometry.Drag(start, SelectionHandle.Inside, -5000, -5000, Screen));
    }

    [Fact]
    public void Dragging_a_corner_resizes_and_clamps()
    {
        var start = new RectD(100, 100, 200, 100);
        Assert.Equal(new RectD(100, 100, 250, 130), SelectionGeometry.Drag(start, SelectionHandle.BottomRight, 50, 30, Screen));
        Assert.Equal(new RectD(100, 100, 900, 500), SelectionGeometry.Drag(start, SelectionHandle.BottomRight, 5000, 5000, Screen));
        Assert.Equal(new RectD(0, 0, 300, 200), SelectionGeometry.Drag(start, SelectionHandle.TopLeft, -500, -500, Screen));
    }

    [Fact]
    public void Dragging_a_side_past_the_opposite_one_flips_the_rectangle()
    {
        var start = new RectD(100, 100, 200, 100);
        var flipped = SelectionGeometry.Drag(start, SelectionHandle.Left, 300, 0, Screen);   // left edge goes to x=400, past the right edge (300)
        Assert.Equal(300, flipped.X);
        Assert.Equal(100, flipped.Width);
        Assert.Equal(100, flipped.Y);
    }

    [Fact]
    public void A_selection_never_gets_smaller_than_the_minimum()
    {
        var start = new RectD(100, 100, 200, 100);
        var tiny = SelectionGeometry.Drag(start, SelectionHandle.Right, -200, 0, Screen);
        Assert.True(tiny.Width >= SelectionGeometry.MinSize);
    }

    [Fact]
    public void Toolbar_goes_below_the_selection_aligned_to_its_right_edge()
    {
        var (x, y) = SelectionGeometry.ToolbarPosition(new RectD(100, 100, 400, 200), 300, 40, Screen);
        Assert.Equal(200, x);
        Assert.Equal(308, y);
    }

    [Fact]
    public void Toolbar_moves_above_or_inside_when_there_is_no_room_below()
    {
        var (_, above) = SelectionGeometry.ToolbarPosition(new RectD(100, 300, 400, 280), 300, 40, Screen);
        Assert.Equal(252, above);

        var (_, inside) = SelectionGeometry.ToolbarPosition(new RectD(0, 0, 1000, 600), 300, 40, Screen);
        Assert.Equal(552, inside);   // full-screen selection: the bar sits inside, at the bottom
    }

    [Fact]
    public void Toolbar_stays_inside_the_screen_horizontally()
    {
        var (x, _) = SelectionGeometry.ToolbarPosition(new RectD(10, 100, 100, 100), 400, 40, Screen);
        Assert.Equal(8, x);
    }

    [Fact]
    public void Arrow_barbs_sit_behind_the_tip_on_both_sides()
    {
        var (left, right) = ArrowGeometry.Barbs(0, 0, 100, 0, 3);
        Assert.True(left.X < 100 && right.X < 100);
        Assert.Equal(left.Y, -right.Y, 6);   // symmetric around the line
        Assert.NotEqual(left.Y, right.Y);
    }

    [Fact]
    public void Arrow_with_no_length_does_not_blow_up()
    {
        var (left, right) = ArrowGeometry.Barbs(5, 5, 5, 5, 3);
        Assert.Equal((5.0, 5.0), left);
        Assert.Equal((5.0, 5.0), right);
    }

    [Fact]
    public void File_name_is_sortable_and_has_no_invalid_characters()
    {
        string name = CaptureNaming.FileName(new DateTime(2026, 11, 25, 14, 3, 7));
        Assert.Equal("Screenshot 2026-11-25 14-03-07.png", name);
        Assert.DoesNotContain(name, c => Path.GetInvalidFileNameChars().Contains(c));
        Assert.EndsWith(".jpg", CaptureNaming.FileName(DateTime.Now, ".jpg"));
    }

    [Fact]
    public void Save_folder_is_the_configured_one_or_pictures_screenshots()
    {
        Assert.Equal(Path.Combine(@"C:\Pics", "Screenshots"), CaptureNaming.SaveFolder("", @"C:\Pics"));
        Assert.Equal(Path.Combine(@"C:\Pics", "Screenshots"), CaptureNaming.SaveFolder("   ", @"C:\Pics"));
        Assert.Equal(@"D:\Prints", CaptureNaming.SaveFolder(@" D:\Prints ", @"C:\Pics"));
    }

    [Fact]
    public void Unique_path_adds_a_counter_when_the_name_is_taken()
    {
        var taken = new HashSet<string>
        {
            Path.Combine("D:\\P", "Screenshot a.png"),
            Path.Combine("D:\\P", "Screenshot a (2).png"),
        };
        Assert.Equal(Path.Combine("D:\\P", "Screenshot b.png"), CaptureNaming.UniquePath("D:\\P", "Screenshot b.png", taken.Contains));
        Assert.Equal(Path.Combine("D:\\P", "Screenshot a (3).png"), CaptureNaming.UniquePath("D:\\P", "Screenshot a.png", taken.Contains));
    }

    [Fact]
    public void New_capture_settings_default_to_quiet_and_shift_print_screen()
    {
        var s = new Apparatus.Core.Settings.AppSettings();
        Assert.False(s.CaptureNotifyOnSave);
        Assert.Equal("", s.CaptureSaveFolder);
        Assert.Equal("Shift+PrintScreen", s.CaptureQuickHotkey);
        Assert.True(HotkeySpec.TryParse(s.CaptureQuickHotkey, out _));
    }

    [Fact]
    public void Union_of_monitors_covers_the_whole_virtual_desktop()
    {
        var left = new RectD(0, 0, 1920, 1080);
        var right = new RectD(1920, 0, 1920, 1080);
        Assert.Equal(new RectD(0, 0, 3840, 1080), RectD.Union(new[] { left, right }));

        // a monitor placed above and to the left (negative coordinates)
        var above = new RectD(-1280, -1024, 1280, 1024);
        Assert.Equal(new RectD(-1280, -1024, 5120, 2104), RectD.Union(new[] { left, right, above }));
        Assert.True(RectD.Union(Array.Empty<RectD>()).IsEmpty);
    }

    [Fact]
    public void Automatic_capture_flashes_by_default()
    {
        Assert.True(new Apparatus.Core.Settings.AppSettings().CaptureAutoFlash);
    }

    [Fact]
    public void Automatic_capture_takes_only_the_focused_monitor_by_default()
    {
        Assert.False(new Apparatus.Core.Settings.AppSettings().CaptureAutoAllMonitors);
    }

    [Fact]
    public void Image_format_defaults_to_png_with_quality_90()
    {
        var s = new Apparatus.Core.Settings.AppSettings();
        Assert.Equal(CaptureImageFormat.Png, CaptureImageFormats.Parse(s.CaptureFormat));
        Assert.Equal(90, s.CaptureQuality);
        Assert.Equal(CaptureImageFormats.DefaultQuality, s.CaptureQuality);
    }

    [Theory]
    [InlineData("png", CaptureImageFormat.Png)]
    [InlineData("PNG", CaptureImageFormat.Png)]
    [InlineData("jpeg", CaptureImageFormat.Jpeg)]
    [InlineData(" JPG ", CaptureImageFormat.Jpeg)]
    [InlineData("webp", CaptureImageFormat.Png)]   // unknown: the default
    [InlineData("", CaptureImageFormat.Png)]
    [InlineData(null, CaptureImageFormat.Png)]
    public void Format_is_read_from_the_setting_text(string? text, CaptureImageFormat expected) =>
        Assert.Equal(expected, CaptureImageFormats.Parse(text));

    [Fact]
    public void Extensions_map_to_formats_and_back()
    {
        Assert.Equal("png", CaptureImageFormats.Extension(CaptureImageFormat.Png));
        Assert.Equal("jpg", CaptureImageFormats.Extension(CaptureImageFormat.Jpeg));
        Assert.Equal(CaptureImageFormat.Jpeg, CaptureImageFormats.FromExtension(".jpeg"));
        Assert.Equal(CaptureImageFormat.Jpeg, CaptureImageFormats.FromExtension(".JPG"));
        Assert.Equal(CaptureImageFormat.Png, CaptureImageFormats.FromExtension(".png"));
        Assert.Equal(CaptureImageFormat.Png, CaptureImageFormats.FromExtension(null));
        Assert.EndsWith(".jpg", CaptureNaming.FileName(DateTime.Now, CaptureImageFormats.Extension(CaptureImageFormat.Jpeg)));
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(90, 90)]
    [InlineData(100, 100)]
    [InlineData(500, 100)]
    public void Quality_stays_between_1_and_100(int given, int expected) =>
        Assert.Equal(expected, CaptureImageFormats.ClampQuality(given));

    [Fact]
    public void Known_capture_programs_are_found_by_process_name()
    {
        var found = KnownCaptureTools.FindRunning(new[] { "explorer", "lightshot", "SnagitEditor", "Snagit32", "chrome" });
        Assert.Equal(new[] { "Lightshot", "Snagit" }, found);
        Assert.Empty(KnownCaptureTools.FindRunning(new[] { "explorer", "chrome" }));
    }

    [Fact]
    public void Print_screen_is_a_valid_hotkey_without_modifiers()
    {
        Assert.True(HotkeySpec.TryParse("PrintScreen", out var spec));
        Assert.Equal("PrintScreen", spec.ToString());
        Assert.True(HotkeySpec.TryParse("Ctrl+PrintScreen", out var withCtrl));
        Assert.Equal("Ctrl+PrintScreen", withCtrl.ToString());
        Assert.False(HotkeySpec.TryParse("A", out _));   // ordinary keys still need a modifier
    }

    [Fact]
    public void Print_screen_tweak_is_internal_and_not_touched_by_restore_all()
    {
        Assert.DoesNotContain(TweakCatalog.All, t => t.Id == TweakCatalog.PrintScreenSnippingId);
        var tweak = TweakCatalog.Get(TweakCatalog.PrintScreenSnippingId);
        Assert.Equal(@"Control Panel\Keyboard", tweak.KeyPath);
        Assert.Equal("PrintScreenKeyForSnippingEnabled", tweak.ValueName);

        var registry = new FakeRegistry();
        registry.WriteDword(tweak.KeyPath, tweak.ValueName, 1);
        string backup = Path.Combine(Path.GetTempPath(), "apparatus-tweaks-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var service = new TweakService(registry, backup);
            Assert.True(service.Apply(tweak.Id, 0).Success);
            Assert.Equal(0, registry.ReadDword(tweak.KeyPath, tweak.ValueName));

            Assert.Equal(0, service.RestoreAll());   // not part of the tweaks page
            Assert.Equal(0, registry.ReadDword(tweak.KeyPath, tweak.ValueName));

            Assert.True(service.Restore(tweak.Id).Success);
            Assert.Equal(1, registry.ReadDword(tweak.KeyPath, tweak.ValueName));
        }
        finally
        {
            File.Delete(backup);
        }
    }

    private sealed class FakeRegistry : ITweakRegistry
    {
        private readonly Dictionary<(string, string), int> _values = new();
        public int? ReadDword(string keyPath, string valueName) => _values.TryGetValue((keyPath, valueName), out var v) ? v : null;
        public void WriteDword(string keyPath, string valueName, int value) => _values[(keyPath, valueName)] = value;
        public void DeleteValue(string keyPath, string valueName) => _values.Remove((keyPath, valueName));
    }
}
