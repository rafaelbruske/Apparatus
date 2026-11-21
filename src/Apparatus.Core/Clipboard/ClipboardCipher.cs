using System.Security.Cryptography;
using System.Text;

namespace Apparatus.Core.Clipboard;

/// <summary>Wraps a small secret so that only the same user on the same machine can unwrap it.</summary>
public interface IKeyProtector
{
    byte[] Protect(byte[] data);

    /// <summary>Throws <see cref="CryptographicException"/> when the data cannot be unwrapped by this user/machine.</summary>
    byte[] Unprotect(byte[] data);
}

/// <summary>Windows DPAPI, scoped to the current user. (Apparatus runs on Windows only, so the platform analyzer is silenced here.)</summary>
public sealed class DpapiKeyProtector : IKeyProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Apparatus.clipboard.v1");

#pragma warning disable CA1416
    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
}

/// <summary>
/// Encrypts clipboard history entries at rest. A random 256-bit master key lives in a file wrapped by the
/// OS (DPAPI); from it two subkeys are derived, one for AES-GCM (the text) and one for HMAC-SHA256 (the
/// fingerprint used to recognise a repeated copy, so that no plain hash of the text is stored).
/// Protects against anyone who reads the files without being you (another account, a stolen disk, a backup);
/// it cannot protect against a program running as you, which can ask DPAPI for the same key.
/// </summary>
public sealed class ClipboardCipher
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12, TagSize = 16, HeaderSize = 1 + NonceSize + TagSize;
    private static readonly byte[] AssociatedData = Encoding.ASCII.GetBytes("apparatus-clip-v1");

    private readonly byte[] _encryptionKey;
    private readonly byte[] _macKey;

    private ClipboardCipher(byte[] masterKey, bool wasReset)
    {
        _encryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: Encoding.ASCII.GetBytes("enc"));
        _macKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: Encoding.ASCII.GetBytes("mac"));
        WasReset = wasReset;
    }

    /// <summary>True when the stored key could not be unwrapped (another account, a reset profile) and a new one was made: older entries are unreadable.</summary>
    public bool WasReset { get; }

    /// <summary>A throw-away key for tests and in-memory stores.</summary>
    public static ClipboardCipher CreateEphemeral() => new(RandomNumberGenerator.GetBytes(32), wasReset: false);

    /// <summary>Loads the wrapped master key from <paramref name="keyFile"/>, creating it on first use.</summary>
    public static ClipboardCipher Open(string keyFile, IKeyProtector protector)
    {
        bool reset = false;
        if (File.Exists(keyFile))
        {
            try
            {
                var key = protector.Unprotect(File.ReadAllBytes(keyFile));
                if (key.Length == 32) return new ClipboardCipher(key, wasReset: false);
                reset = true;
            }
            catch (Exception ex) when (ex is CryptographicException or IOException)
            {
                AppLog.Warn($"The clipboard key could not be read ({ex.GetType().Name}); a new one will be created and older entries are lost");
                reset = true;
            }
        }

        var fresh = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyFile))!);
        string temp = keyFile + ".tmp";
        File.WriteAllBytes(temp, protector.Protect(fresh));
        File.Move(temp, keyFile, overwrite: true);
        return new ClipboardCipher(fresh, reset);
    }

    /// <summary>version | nonce | tag | ciphertext. A new random nonce each time, so equal texts never look equal on disk.</summary>
    public byte[] Seal(string text)
    {
        byte[] plain = Encoding.UTF8.GetBytes(text);
        var blob = new byte[HeaderSize + plain.Length];
        blob[0] = FormatVersion;
        var nonce = blob.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_encryptionKey, TagSize);
        aes.Encrypt(nonce, plain, blob.AsSpan(HeaderSize), blob.AsSpan(1 + NonceSize, TagSize), AssociatedData);
        CryptographicOperations.ZeroMemory(plain);
        return blob;
    }

    /// <summary>The text, or null when the blob is malformed, was made with another key or was tampered with.</summary>
    public string? Open(byte[] blob)
    {
        if (blob.Length < HeaderSize || blob[0] != FormatVersion) return null;
        var plain = new byte[blob.Length - HeaderSize];
        try
        {
            using var aes = new AesGcm(_encryptionKey, TagSize);
            aes.Decrypt(blob.AsSpan(1, NonceSize), blob.AsSpan(HeaderSize), blob.AsSpan(1 + NonceSize, TagSize), plain, AssociatedData);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Stable identifier of a text under this key (an HMAC): equal texts match, nothing else can be learned from it.</summary>
    public string Fingerprint(string text) =>
        Convert.ToHexString(HMACSHA256.HashData(_macKey, Encoding.UTF8.GetBytes(text)));
}
