using System.Security.Cryptography;
using System.Text;
using Apparatus.Core.Clipboard;
using Microsoft.Data.Sqlite;

namespace Apparatus.Core.Tests;

/// <summary>A reversible stand-in for DPAPI, so the key handling can be tested without tying tests to one Windows account.</summary>
public sealed class FakeKeyProtector : IKeyProtector
{
    private readonly byte _mask;

    public FakeKeyProtector(byte mask = 0x5A) => _mask = mask;

    public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ _mask)).Append(_mask).ToArray();

    public byte[] Unprotect(byte[] data)
    {
        if (data.Length == 0 || data[^1] != _mask) throw new CryptographicException("not mine");
        return data[..^1].Select(b => (byte)(b ^ _mask)).ToArray();
    }
}

public class ClipboardCipherTests
{
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("Olá, ação ✓ — 日本語 😀")]
    [InlineData("linha 1\r\nlinha 2\t\0fim")]
    public void SealAndOpen_RoundTrip(string text)
    {
        var cipher = ClipboardCipher.CreateEphemeral();
        Assert.Equal(text, cipher.Open(cipher.Seal(text)));
    }

    [Fact]
    public void LargeText_RoundTrips()
    {
        var cipher = ClipboardCipher.CreateEphemeral();
        string text = new string('x', 64 * 1024) + "ç";
        Assert.Equal(text, cipher.Open(cipher.Seal(text)));
    }

    [Fact]
    public void TheCiphertext_DoesNotContainThePlainText_AndDiffersEachTime()
    {
        var cipher = ClipboardCipher.CreateEphemeral();
        const string secret = "senha-super-secreta-12345";
        byte[] a = cipher.Seal(secret), b = cipher.Seal(secret);

        Assert.False(a.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0);
        Assert.NotEqual(a, b);   // a fresh nonce: equal texts never look equal on disk
        Assert.Equal(secret, cipher.Open(a));
        Assert.Equal(secret, cipher.Open(b));
    }

    [Fact]
    public void TamperedOrForeignBlobs_AreRejected_NotMisread()
    {
        var cipher = ClipboardCipher.CreateEphemeral();
        byte[] blob = cipher.Seal("texto");

        for (int i = 0; i < blob.Length; i += Math.Max(1, blob.Length / 8))
        {
            byte[] copy = (byte[])blob.Clone();
            copy[i] ^= 0x01;
            Assert.Null(cipher.Open(copy));          // any flipped byte (version, nonce, tag or data) is detected
        }
        Assert.Null(cipher.Open(Array.Empty<byte>()));
        Assert.Null(cipher.Open(new byte[10]));
        Assert.Null(ClipboardCipher.CreateEphemeral().Open(blob));   // another key
    }

    [Fact]
    public void Fingerprint_IsStable_ButDependsOnTheKey()
    {
        var a = ClipboardCipher.CreateEphemeral();
        var b = ClipboardCipher.CreateEphemeral();
        Assert.Equal(a.Fingerprint("x"), a.Fingerprint("x"));
        Assert.NotEqual(a.Fingerprint("x"), a.Fingerprint("y"));
        Assert.NotEqual(a.Fingerprint("x"), b.Fingerprint("x"));
        Assert.DoesNotContain(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("x"))), a.Fingerprint("x"));   // not a plain hash
    }

    [Fact]
    public void TheKey_PersistsAcrossRuns_AndIsStoredWrapped()
    {
        using var dir = new TempDir();
        string keyFile = Path.Combine(dir.Path, "clipboard.key");
        var first = ClipboardCipher.Open(keyFile, new FakeKeyProtector());
        byte[] blob = first.Seal("guardado");

        var second = ClipboardCipher.Open(keyFile, new FakeKeyProtector());
        Assert.False(second.WasReset);
        Assert.Equal("guardado", second.Open(blob));
        Assert.Equal(first.Fingerprint("a"), second.Fingerprint("a"));

        // the file holds the wrapped key, not the raw one: the "protector" here XORs, so a raw key would show as 32 bytes
        Assert.Equal(33, new FileInfo(keyFile).Length);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void WhenTheKeyCannotBeUnwrapped_ANewOneIsMade_AndOldDataIsUnreadable()
    {
        using var dir = new TempDir();
        string keyFile = Path.Combine(dir.Path, "clipboard.key");
        var original = ClipboardCipher.Open(keyFile, new FakeKeyProtector(0x11));
        byte[] blob = original.Seal("segredo");

        var other = ClipboardCipher.Open(keyFile, new FakeKeyProtector(0x22));   // e.g. another Windows account
        Assert.True(other.WasReset);
        Assert.Null(other.Open(blob));
        Assert.Equal("novo", other.Open(other.Seal("novo")));
        Assert.False(ClipboardCipher.Open(keyFile, new FakeKeyProtector(0x22)).WasReset);   // and the new key sticks
    }

    // ------------------------------------------------------------ real DPAPI

    [Fact]
    public void RealDpapi_WrapsAndUnwraps_ForTheCurrentUser()
    {
        var dpapi = new DpapiKeyProtector();
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        byte[] wrapped = dpapi.Protect(secret);

        Assert.True(wrapped.Length > secret.Length);
        Assert.False(wrapped.AsSpan().IndexOf(secret) >= 0);
        Assert.Equal(secret, dpapi.Unprotect(wrapped));
        Assert.ThrowsAny<CryptographicException>(() => dpapi.Unprotect(new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void RealDpapi_WorksWithTheCipher_EndToEnd()
    {
        using var dir = new TempDir();
        string keyFile = Path.Combine(dir.Path, "clipboard.key");
        byte[] blob = ClipboardCipher.Open(keyFile, new DpapiKeyProtector()).Seal("com dpapi de verdade");
        Assert.Equal("com dpapi de verdade", ClipboardCipher.Open(keyFile, new DpapiKeyProtector()).Open(blob));
    }
}

public class EncryptedClipboardStoreTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Every byte the database wrote to disk (main file and write-ahead log).</summary>
    private static byte[] DiskBytes(string dbPath)
    {
        SqliteConnection.ClearAllPools();   // let SQLite checkpoint and release the files
        var all = new List<byte>();
        foreach (var file in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (File.Exists(file)) all.AddRange(File.ReadAllBytes(file));
        }
        return all.ToArray();
    }

    private static bool Contains(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0 || haystack.AsSpan().IndexOf(Encoding.Unicode.GetBytes(needle)) >= 0;

    [Fact]
    public void TextNeverReachesTheDisk_InClear()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "c.db");
        var cipher = ClipboardCipher.Open(Path.Combine(dir.Path, "k"), new FakeKeyProtector());
        using (var store = new ClipboardHistoryStore(path, cipher))
        {
            store.Add("MARCADOR-SECRETO-ALFA", "Chrome", T0);
            store.Add("MARCADOR-SECRETO-BETA com acentuação ação", "Code", T0.AddSeconds(1));
            Assert.Equal(2, store.All.Count);   // readable through the store...
        }
        byte[] disk = DiskBytes(path);
        Assert.NotEmpty(disk);
        Assert.False(Contains(disk, "MARCADOR-SECRETO-ALFA"));   // ...but not on disk
        Assert.False(Contains(disk, "MARCADOR-SECRETO-BETA"));
        Assert.False(Contains(disk, "acentuação"));
    }

    [Fact]
    public void DeletedEntries_AreScrubbedFromTheFile()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "c.db");
        var cipher = ClipboardCipher.Open(Path.Combine(dir.Path, "k"), new FakeKeyProtector());
        using (var store = new ClipboardHistoryStore(path, cipher))
        {
            var entry = store.Add("APAGADO-DEVE-SUMIR", "x", T0)!;
            store.Delete(entry.Id);
            Assert.Empty(store.All);
        }
        Assert.False(Contains(DiskBytes(path), "APAGADO-DEVE-SUMIR"));
    }

    [Fact]
    public void Persists_AndDecrypts_AfterReopen()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "c.db");
        string key = Path.Combine(dir.Path, "k");
        using (var store = new ClipboardHistoryStore(path, ClipboardCipher.Open(key, new FakeKeyProtector())))
        {
            store.SetPinned(store.Add("fixado", "Code", T0)!.Id, true);
            store.Add("fixado", "Code", T0.AddMinutes(1));
        }
        using var again = new ClipboardHistoryStore(path, ClipboardCipher.Open(key, new FakeKeyProtector()));
        var e = Assert.Single(again.All);
        Assert.Equal("fixado", e.Text);
        Assert.Equal(2, e.Count);
        Assert.True(e.Pinned);
        Assert.Equal(T0.AddMinutes(1), e.LastUtc);
    }

    [Fact]
    public void RecognisesARepeatedCopy_ThroughTheKeyedFingerprint()
    {
        using var store = new ClipboardHistoryStore(":memory:", ClipboardCipher.CreateEphemeral());
        long first = store.Add("mesmo texto", "a", T0)!.Id;
        long second = store.Add("mesmo texto", "b", T0.AddSeconds(5))!.Id;
        Assert.Equal(first, second);
        Assert.Equal(2, store.All.Single().Count);
    }

    [Fact]
    public void ALostKey_DropsTheUnreadableEntries_AndKeepsWorking()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "c.db");
        string key = Path.Combine(dir.Path, "k");
        using (var store = new ClipboardHistoryStore(path, ClipboardCipher.Open(key, new FakeKeyProtector(0x11))))
            store.Add("de outra conta", "x", T0);

        var replaced = ClipboardCipher.Open(key, new FakeKeyProtector(0x22));   // the old key cannot be unwrapped any more
        Assert.True(replaced.WasReset);
        using var again = new ClipboardHistoryStore(path, replaced);
        Assert.Empty(again.All);                                                 // gone, not garbage
        Assert.NotNull(again.Add("recomeçando", "x", T0.AddMinutes(1)));
        Assert.Equal("recomeçando", again.All.Single().Text);
    }

    [Fact]
    public void ATamperedRow_IsDropped_NotShownAsGarbage()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "c.db");
        var cipher = ClipboardCipher.Open(Path.Combine(dir.Path, "k"), new FakeKeyProtector());
        using (var store = new ClipboardHistoryStore(path, cipher))
        {
            store.Add("íntegro", "x", T0);
            store.Add("vai ser adulterado", "x", T0.AddSeconds(1));
        }
        SqliteConnection.ClearAllPools();
        using (var raw = new SqliteConnection($"Data Source={path}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "UPDATE clips SET blob = zeroblob(40) WHERE id = 2";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var again = new ClipboardHistoryStore(path, cipher);
        Assert.Equal(new[] { "íntegro" }, again.All.Select(e => e.Text).ToArray());
    }

    [Fact]
    public void WithoutAKey_NothingIsStored_AndNoFileIsWritten()
    {
        using var dir = new TempDir();
        using var store = ClipboardHistoryStore.CreateUnavailable();
        Assert.False(store.IsAvailable);
        Assert.Null(store.Add("não deve ser guardado", "x", T0));
        Assert.Empty(store.All);
        Assert.Empty(Directory.GetFiles(dir.Path));

        // even when a path is given, a missing cipher must not produce a plain-text file
        string path = Path.Combine(dir.Path, "c.db");
        using var withPath = new ClipboardHistoryStore(path, cipher: null);
        withPath.Add("também não", "x");
        Assert.False(File.Exists(path));
    }

    // -------------------------------------------------------------- migration

    private static void CreateVersion1Database(string path, params (string Text, string Source, long First, long Last, int Count, int Pinned)[] rows)
    {
        using var raw = new SqliteConnection($"Data Source={path}");
        raw.Open();
        void Run(string sql, Action<SqliteCommand>? bind = null)
        {
            using var cmd = raw.CreateCommand();
            cmd.CommandText = sql;
            bind?.Invoke(cmd);
            cmd.ExecuteNonQuery();
        }
        Run("PRAGMA journal_mode=WAL;");
        Run("""
            CREATE TABLE clips(
                id INTEGER PRIMARY KEY AUTOINCREMENT, hash TEXT NOT NULL UNIQUE, text TEXT NOT NULL,
                source TEXT NOT NULL DEFAULT '', first_used INTEGER NOT NULL, last_used INTEGER NOT NULL,
                count INTEGER NOT NULL DEFAULT 1, pinned INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX ix_clips_last ON clips(last_used DESC);
            PRAGMA user_version = 1;
            """);
        foreach (var r in rows)
        {
            Run("INSERT INTO clips(hash, text, source, first_used, last_used, count, pinned) VALUES($h, $t, $s, $f, $l, $c, $p)", cmd =>
            {
                cmd.Parameters.AddWithValue("$h", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(r.Text))));
                cmd.Parameters.AddWithValue("$t", r.Text);
                cmd.Parameters.AddWithValue("$s", r.Source);
                cmd.Parameters.AddWithValue("$f", r.First);
                cmd.Parameters.AddWithValue("$l", r.Last);
                cmd.Parameters.AddWithValue("$c", r.Count);
                cmd.Parameters.AddWithValue("$p", r.Pinned);
            });
        }
    }

    [Fact]
    public void AnOldPlainTextDatabase_IsEncrypted_PreservingEverything_AndScrubbed()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "c.db");
        long t = new DateTimeOffset(T0).ToUnixTimeMilliseconds();
        CreateVersion1Database(path,
            ("ANTIGO-EM-TEXTO-CLARO-UM", "Chrome", t, t + 1000, 3, 1),
            ("ANTIGO-EM-TEXTO-CLARO-DOIS acentuação", "Code", t, t + 2000, 1, 0));
        Assert.True(Contains(DiskBytes(path), "ANTIGO-EM-TEXTO-CLARO-UM"));   // the premise: it really was in clear

        var cipher = ClipboardCipher.Open(Path.Combine(dir.Path, "k"), new FakeKeyProtector());
        using (var store = new ClipboardHistoryStore(path, cipher))
        {
            Assert.Equal(new[] { "ANTIGO-EM-TEXTO-CLARO-DOIS acentuação", "ANTIGO-EM-TEXTO-CLARO-UM" }, store.All.Select(e => e.Text).ToArray());
            var um = store.All.Single(e => e.Text.EndsWith("UM"));
            Assert.Equal(3, um.Count);
            Assert.True(um.Pinned);
            Assert.Equal("Chrome", um.Source);
            Assert.Equal(T0.AddSeconds(1), um.LastUtc);

            // still fully usable afterwards
            Assert.NotNull(store.Add("novo depois da migração", "x", T0.AddMinutes(5)));
            Assert.Equal(3, store.All.Count);
        }

        byte[] disk = DiskBytes(path);
        Assert.False(Contains(disk, "ANTIGO-EM-TEXTO-CLARO-UM"));
        Assert.False(Contains(disk, "ANTIGO-EM-TEXTO-CLARO-DOIS"));

        // and reopening does not migrate (or lose) anything a second time
        using var reopened = new ClipboardHistoryStore(path, cipher);
        Assert.Equal(3, reopened.All.Count);
    }

    [Fact]
    public void AnOldDatabase_WithNoKey_IsNotMigratedAndStaysOutOfUse()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "c.db");
        CreateVersion1Database(path, ("fica como está", "x", 1, 2, 1, 0));
        using var store = new ClipboardHistoryStore(path, cipher: null);
        Assert.False(store.IsAvailable);
        Assert.Empty(store.All);   // cipher == null never even opens the old file
    }
}
