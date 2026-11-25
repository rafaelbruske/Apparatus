using System.Runtime.InteropServices.WindowsRuntime;
using Apparatus.Core.Capture;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Apparatus.App;

/// <summary>Turns a grabbed picture (top-down BGRA) into PNG or JPEG bytes.</summary>
public static class CaptureImage
{
    /// <summary>PNG is lossless and ignores <paramref name="quality"/>; JPEG uses it (1 to 100).</summary>
    public static async Task<byte[]> EncodeAsync(byte[] bgra, int width, int height, CaptureImageFormat format, int quality)
    {
        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder;
        if (format == CaptureImageFormat.Jpeg)
        {
            var options = new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue(CaptureImageFormats.ClampQuality(quality) / 100f, PropertyType.Single) },
            };
            encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options);
        }
        else
        {
            encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        }

        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();
        stream.Seek(0);
        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }
}
