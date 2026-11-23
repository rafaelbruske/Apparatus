using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apparatus.Core.Clipboard;

namespace Apparatus.Core.Assistant;

/// <summary>
/// API keys, one per provider, in a single file wrapped by the OS (DPAPI, current user). The file lives in the
/// local (non-roaming) app folder and is never part of a backup or the sync folder; keys are never written to
/// settings.json. Protects against another account, a stolen disk or a copied backup; it cannot protect against a
/// program running as you, which can ask DPAPI for the same data.
/// </summary>
public sealed class AssistantKeyStore
{
    /// <summary>The purpose label given to DPAPI; keys wrapped for the clipboard cannot be read as assistant keys.</summary>
    public const string Purpose = "Apparatus.assistant.keys.v1";

    private readonly string _path;
    private readonly IKeyProtector _protector;
    private readonly object _gate = new();

    public AssistantKeyStore(string path, IKeyProtector protector)
    {
        _path = path;
        _protector = protector;
    }

    /// <summary>True when the file existed but could not be unwrapped (another account, a reset profile). The keys are lost; the user pastes them again.</summary>
    public bool WasReset { get; private set; }

    public static AssistantKeyStore CreateDefault(string path) => new(path, new DpapiKeyProtector(Purpose));

    public bool Has(string providerId) => Get(providerId) is not null;

    /// <summary>The key, or null when none is saved.</summary>
    public string? Get(string providerId)
    {
        lock (_gate)
        {
            return Read().TryGetValue(Normalize(providerId), out var key) && key.Length > 0 ? key : null;
        }
    }

    public void Set(string providerId, string key)
    {
        key = key.Trim();
        if (key.Length == 0) { Remove(providerId); return; }
        if (key.Any(char.IsControl)) throw new ArgumentException("A chave não pode conter quebras de linha ou caracteres de controle.", nameof(key));

        lock (_gate)
        {
            var all = Read();
            all[Normalize(providerId)] = key;
            Write(all);
        }
    }

    public void Remove(string providerId)
    {
        lock (_gate)
        {
            var all = Read();
            if (all.Remove(Normalize(providerId))) Write(all);
        }
    }

    /// <summary>Forgets every key (deletes the file).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            try { File.Delete(_path); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>Ids of the providers that have a key, never the keys themselves.</summary>
    public IReadOnlyList<string> ProvidersWithKey()
    {
        lock (_gate) return Read().Where(kv => kv.Value.Length > 0).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    private static string Normalize(string providerId) => providerId.Trim().ToLowerInvariant();

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            byte[] plain = _protector.Unprotect(File.ReadAllBytes(_path));
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or JsonException)
        {
            AppLog.Warn($"The assistant key file could not be read ({ex.GetType().Name}); the keys must be entered again");
            WasReset = true;
            return new();
        }
    }

    private void Write(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(all);
        try
        {
            string temp = _path + ".tmp";
            File.WriteAllBytes(temp, _protector.Protect(plain));
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
