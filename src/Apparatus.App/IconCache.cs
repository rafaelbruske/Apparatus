using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Apparatus.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Apparatus.App;

/// <summary>Loads shell icons off the UI thread and caches the resulting bitmaps.</summary>
public sealed class IconCache
{
    private const int MaxEntries = 400;
    private const int IconPixels = 48;

    private readonly DispatcherQueue _ui;
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _cache = new();
    private readonly SemaphoreSlim _gate = new(3);

    public IconCache(DispatcherQueue ui) => _ui = ui;

    /// <param name="key">"shell:&lt;parsing name&gt;" or "path:&lt;file path&gt;".</param>
    public async Task<ImageSource?> GetAsync(string key)
    {
        if (_cache.Count > MaxEntries) _cache.Clear();
        var task = _cache.GetOrAdd(key, Load);
        var image = await task.ConfigureAwait(false);
        // A failure is not remembered: the next time the icon is wanted it is tried again.
        if (image is null) _cache.TryRemove(new KeyValuePair<string, Task<ImageSource?>>(key, task));
        return image;
    }

    private async Task<ImageSource?> Load(string key)
    {
        int colon = key.IndexOf(':');
        if (colon < 0) return null;
        string parsingName = key[(colon + 1)..];

        IconBitmap? pixels;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            pixels = await Task.Run(() => ShellIcons.TryGet(parsingName, IconPixels)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        if (pixels is null) return null;

        var tcs = new TaskCompletionSource<ImageSource?>();
        if (!_ui.TryEnqueue(() =>
            {
                try
                {
                    var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
                    bitmap.PixelBuffer.AsStream().Write(pixels.Bgra, 0, pixels.Bgra.Length);
                    bitmap.Invalidate();
                    tcs.SetResult(bitmap);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
        {
            return null;
        }
        return await tcs.Task.ConfigureAwait(false);
    }
}
