using System.Text;

namespace Apparatus.Core.Start;

/// <summary>
/// The "Tasks" of an app's jump list (the entries Windows shows above the recent files when you right-click its taskbar button:
/// "New window", "New private window"...). Apps write them to <c>%APPDATA%\Microsoft\Windows\Recent\CustomDestinations</c>,
/// one file per app named after a hash of its AppUserModelID. This class finds the file and cuts the shortcuts out of it.
/// </summary>
public static class JumpLists
{
    public const string FileExtension = ".customDestinations-ms";

    private const ulong Polynomial = 0x259c84cba6426349;   // CRC-64, reflected, as Windows uses for jump list names

    /// <summary>The file name Windows gives to the jump list of <paramref name="appId"/> (16 hex digits at most, no extension).</summary>
    public static string HashOf(string appId)
    {
        ulong crc = ulong.MaxValue;
        foreach (byte b in Encoding.Unicode.GetBytes(appId.ToUpperInvariant()))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ ReversedPolynomial : crc >> 1;
        }
        return crc.ToString("x");
    }

    // The polynomial above is written in the normal form; the loop runs on the reflected one.
    private static readonly ulong ReversedPolynomial = Reflect(Polynomial);

    private static ulong Reflect(ulong value)
    {
        ulong result = 0;
        for (int i = 0; i < 64; i++)
            if ((value >> i & 1) != 0) result |= 1UL << (63 - i);
        return result;
    }

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Recent", "CustomDestinations");

    public static string FileFor(string appId, string? folder = null) =>
        Path.Combine(folder ?? DefaultFolder, HashOf(appId) + FileExtension);

    // "4C 00 00 00" (the size of a shell link header) followed by the CLSID of a shell link.
    private static readonly byte[] LinkSignature =
        { 0x4C, 0, 0, 0, 0x01, 0x14, 0x02, 0, 0, 0, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46 };

    private static readonly byte[] CategoryEnd = { 0xAB, 0xFB, 0xBF, 0xBA };

    /// <summary>
    /// The shortcuts of the first category of the file (the tasks), each as the bytes of a ".lnk" file. The file is a header, then
    /// categories made of shortcuts and ended by the marker AB FB BF BA; the tasks come first and have no title.
    /// </summary>
    public static IReadOnlyList<byte[]> TaskShortcuts(byte[] file)
    {
        int end = IndexOf(file, CategoryEnd, 0);
        if (end < 0) return Array.Empty<byte[]>();

        var starts = new List<int>();
        for (int at = IndexOf(file, LinkSignature, 0); at >= 0 && at < end; at = IndexOf(file, LinkSignature, at + LinkSignature.Length))
            starts.Add(at);

        var result = new List<byte[]>();
        for (int i = 0; i < starts.Count; i++)
        {
            // The next shortcut is preceded by its own 16-byte class id, which is not part of this one.
            int stop = i + 1 < starts.Count ? starts[i + 1] - 16 : end;
            if (stop > starts[i]) result.Add(file[starts[i]..stop]);
        }
        return result;
    }

    /// <summary>True when the app has a jump list file with at least one task. Cheap: no shell calls.</summary>
    public static bool HasTasks(string appId, string? folder = null)
    {
        try
        {
            string path = FileFor(appId, folder);
            return File.Exists(path) && TaskShortcuts(File.ReadAllBytes(path)).Count > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int IndexOf(byte[] data, byte[] pattern, int from)
    {
        for (int i = from; i <= data.Length - pattern.Length; i++)
        {
            int j = 0;
            while (j < pattern.Length && data[i + j] == pattern[j]) j++;
            if (j == pattern.Length) return i;
        }
        return -1;
    }
}
