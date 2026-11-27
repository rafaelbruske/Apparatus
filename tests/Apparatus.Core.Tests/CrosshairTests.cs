using Apparatus.Core.Capture;

namespace Apparatus.Core.Tests;

public class CrosshairTests
{
    private enum Pixel { Clear, White, Black, Other }

    private static Pixel At(CursorImage image, int x, int y)
    {
        int i = (y * image.Size + x) * 4;
        byte b = image.Bgra[i], g = image.Bgra[i + 1], r = image.Bgra[i + 2], a = image.Bgra[i + 3];
        if (a == 0) return Pixel.Clear;
        if (a != 0xFF || b != g || g != r) return Pixel.Other;
        return r == 0xFF ? Pixel.White : r == 0 ? Pixel.Black : Pixel.Other;
    }

    [Theory]
    [InlineData(1.0, 32, 32)]
    [InlineData(1.25, 32, 40)]
    [InlineData(1.5, 32, 48)]
    [InlineData(2.0, 32, 64)]
    [InlineData(1.25, 48, 60)]     // a larger pointer chosen in Windows
    [InlineData(3.0, 256, 256)]    // never beyond what a pointer can be
    [InlineData(1.0, 16, 32)]      // nor smaller than the ordinary one
    [InlineData(1.5, 0, 48)]       // no pointer size known
    [InlineData(0.0, 32, 32)]      // no scale known
    public void SizeFor_FollowsTheScaleAndThePointerSize(double scale, int pointerSize, int expected) =>
        Assert.Equal(expected, Crosshair.SizeFor(scale, pointerSize));

    [Theory]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(96)]
    [InlineData(128)]
    [InlineData(256)]
    public void Draw_IsAWhiteCrossAroundTheHotspot_WithABlackEdge(int size)
    {
        var image = Crosshair.Draw(size);
        Assert.Equal(size, image.Size);
        Assert.Equal(size * size * 4, image.Bgra.Length);
        Assert.Equal((size / 2, size / 2), (image.HotspotX, image.HotspotY));
        Assert.Equal(Pixel.White, At(image, image.HotspotX, image.HotspotY));

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var pixel = At(image, x, y);
                Assert.NotEqual(Pixel.Other, pixel);   // only opaque white, opaque black or nothing

                // the same to the left and right of the hotspot, above and below it, and the two bars alike
                int mx = 2 * image.HotspotX - x, my = 2 * image.HotspotY - y;
                if (mx < size) Assert.Equal(pixel, At(image, mx, y));
                if (my < size) Assert.Equal(pixel, At(image, x, my));
                Assert.Equal(pixel, At(image, y, x));

                if (pixel != Pixel.White) continue;
                // no white pixel touches the background: the black edge is complete
                for (int ny = y - 1; ny <= y + 1; ny++)
                    for (int nx = x - 1; nx <= x + 1; nx++)
                        Assert.NotEqual(Pixel.Clear, At(image, nx, ny));
            }
        }
    }

    [Fact]
    public void Draw_At100Percent_HasAOnePixelLineThatLeavesTheCornersFree()
    {
        var image = Crosshair.Draw(32);
        var row = Enumerable.Range(0, 32).Select(x => At(image, x, 6)).ToArray();   // across the vertical bar, away from the middle
        Assert.Equal(new[] { Pixel.Black, Pixel.White, Pixel.Black }, row.Skip(15).Take(3));
        Assert.Equal(3, row.Count(p => p != Pixel.Clear));
        Assert.Equal(25, Enumerable.Range(0, 32).Count(y => At(image, 16, y) == Pixel.White));
        Assert.Equal(Pixel.Clear, At(image, 0, 0));
        Assert.Equal(Pixel.Clear, At(image, 31, 31));
    }
}
