using System.Security.Cryptography;
using System.Text;
using Apparatus.Core.Assistant;
using Apparatus.Core.Clipboard;

namespace Apparatus.Core.Tests;

public class AssistantKeyStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apparatus-keys-" + Guid.NewGuid().ToString("N"));
    private string KeyFile => Path.Combine(_dir, "assistant.keys");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public void SetAndGet_RoundTrip_PerProvider()
    {
        var store = new AssistantKeyStore(KeyFile, new FakeKeyProtector());
        store.Set("gemini", "AIza-gemini-secret-123");
        store.Set("groq", "gsk_groq-secret-456");

        Assert.Equal("AIza-gemini-secret-123", store.Get("gemini"));
        Assert.Equal("gsk_groq-secret-456", store.Get("groq"));
        Assert.Null(store.Get("openai"));
        Assert.Equal(new[] { "gemini", "groq" }, store.ProvidersWithKey());
    }

    [Fact]
    public void Keys_SurviveANewInstance()
    {
        new AssistantKeyStore(KeyFile, new FakeKeyProtector()).Set("gemini", "AIza-persisted-key");
        Assert.Equal("AIza-persisted-key", new AssistantKeyStore(KeyFile, new FakeKeyProtector()).Get("gemini"));
    }

    [Fact]
    public void FileOnDisk_NeverContainsThePlainKey()
    {
        var store = new AssistantKeyStore(KeyFile, new DpapiKeyProtector(AssistantKeyStore.Purpose));
        store.Set("gemini", "AIza-must-not-appear-in-the-file");

        byte[] raw = File.ReadAllBytes(KeyFile);
        Assert.DoesNotContain("AIza-must-not-appear", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("gemini", Encoding.UTF8.GetString(raw));
        Assert.Equal("AIza-must-not-appear-in-the-file", store.Get("gemini"));
    }

    [Fact]
    public void ProviderIds_AreCaseInsensitive_AndKeysAreTrimmed()
    {
        var store = new AssistantKeyStore(KeyFile, new FakeKeyProtector());
        store.Set("  Gemini ", "  key-with-spaces-123  ");
        Assert.Equal("key-with-spaces-123", store.Get("GEMINI"));
    }

    [Fact]
    public void Remove_And_EmptyKey_ForgetTheProvider()
    {
        var store = new AssistantKeyStore(KeyFile, new FakeKeyProtector());
        store.Set("a", "key-aaaaaa");
        store.Set("b", "key-bbbbbb");
        store.Remove("a");
        store.Set("b", "   ");
        Assert.False(store.Has("a"));
        Assert.False(store.Has("b"));
    }

    [Fact]
    public void KeyWithControlCharacters_IsRefused()
    {
        var store = new AssistantKeyStore(KeyFile, new FakeKeyProtector());
        Assert.Throws<ArgumentException>(() => store.Set("gemini", "abc\r\ndef"));
        Assert.Throws<ArgumentException>(() => store.Set("gemini", "abc\0def"));
    }

    [Fact]
    public void FileFromAnotherUser_ReadsAsEmpty_AndIsFlagged_AndCanBeOverwritten()
    {
        new AssistantKeyStore(KeyFile, new FakeKeyProtector(0x11)).Set("gemini", "someone-elses-key");

        var mine = new AssistantKeyStore(KeyFile, new FakeKeyProtector(0x22));
        Assert.Null(mine.Get("gemini"));
        Assert.True(mine.WasReset);

        mine.Set("gemini", "my-own-key-123");
        Assert.Equal("my-own-key-123", mine.Get("gemini"));
    }

    [Fact]
    public void TamperedFile_ReadsAsEmpty_WithoutThrowing()
    {
        var store = new AssistantKeyStore(KeyFile, new FakeKeyProtector());
        store.Set("gemini", "key-123456");
        File.WriteAllBytes(KeyFile, new byte[] { 1, 2, 3, 4 });
        Assert.Null(store.Get("gemini"));
    }

    [Fact]
    public void Clear_DeletesTheFile()
    {
        var store = new AssistantKeyStore(KeyFile, new FakeKeyProtector());
        store.Set("gemini", "key-123456");
        store.Clear();
        Assert.False(File.Exists(KeyFile));
        Assert.Null(store.Get("gemini"));
    }

    [Fact]
    public void Dpapi_DataWrappedForOnePurpose_CannotBeReadByAnother()
    {
        var clip = new DpapiKeyProtector();                       // the clipboard's label, unchanged
        var assistant = new DpapiKeyProtector(AssistantKeyStore.Purpose);
        byte[] wrapped = assistant.Protect(Encoding.UTF8.GetBytes("secret"));

        Assert.Equal("secret", Encoding.UTF8.GetString(assistant.Unprotect(wrapped)));
        Assert.ThrowsAny<CryptographicException>(() => clip.Unprotect(wrapped));
        Assert.Equal("Apparatus.clipboard.v1", DpapiKeyProtector.ClipboardPurpose);
    }
}

public class AssistantEndpointTests
{
    [Theory]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai/")]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("http://localhost:11434/v1/")]
    [InlineData("http://127.0.0.1:1234/v1")]
    [InlineData("http://[::1]:11434/v1")]
    [InlineData("http://ollama.localhost/v1")]
    public void Accepts_Https_AndLocalHttp(string url)
    {
        Assert.Null(AssistantEndpoint.Validate(url, out var uri));
        Assert.NotNull(uri);
        Assert.EndsWith("/", uri!.AbsolutePath);   // the SDK appends "chat/completions" to it
    }

    [Theory]
    [InlineData("http://api.example.com/v1")]       // the key would cross the network in the clear
    [InlineData("http://192.168.0.10:11434/v1")]    // another machine on the LAN is not "local"
    [InlineData("ftp://example.com/")]
    [InlineData("https://user:pass@example.com/v1")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Rejects_Unsafe_Or_Malformed(string url)
    {
        Assert.NotNull(AssistantEndpoint.Validate(url, out var uri));
        Assert.Null(uri);
    }

    [Fact]
    public void Create_RefusesAnInsecureAddress_BeforeAnyKeyIsUsed()
    {
        var ex = Assert.Throws<AssistantException>(() => ChatClientAdapter.Create("http://evil.example.com/v1", "key-123456", "m"));
        Assert.Equal(AssistantErrorKind.InvalidEndpoint, ex.Kind);
    }

    [Fact]
    public void Create_NeedsAKey_ExceptForLocalServers()
    {
        var ex = Assert.Throws<AssistantException>(() => ChatClientAdapter.Create("https://api.openai.com/v1/", "", "gpt"));
        Assert.Equal(AssistantErrorKind.InvalidKey, ex.Kind);
        Assert.NotNull(ChatClientAdapter.Create("http://localhost:11434/v1/", null, "llama3.1"));
    }

    [Fact]
    public void Create_NeedsAModel()
    {
        var ex = Assert.Throws<AssistantException>(() => ChatClientAdapter.Create("https://api.openai.com/v1/", "key-123456", " "));
        Assert.Equal(AssistantErrorKind.ModelNotFound, ex.Kind);
    }
}

public class ProviderCatalogTests
{
    [Fact]
    public void EmbeddedCatalog_Loads_WithTheExpectedProviders()
    {
        var catalog = ProviderCatalog.LoadEmbedded();
        var ids = catalog.All.Select(p => p.Id).ToList();
        Assert.Contains("gemini", ids);
        Assert.Contains("ollama", ids);
        Assert.Contains("groq", ids);
        Assert.Contains("openrouter", ids);
        Assert.Contains("openai", ids);
        Assert.Contains("custom", ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void GeminiAndOllama_AreHighlightedAsFree()
    {
        var catalog = ProviderCatalog.LoadEmbedded();
        Assert.Equal("Grátis", catalog.Find("gemini")!.Badge);
        Assert.Equal("Grátis", catalog.Find("ollama")!.Badge);
        Assert.False(catalog.Find("ollama")!.NeedsKey);
        Assert.True(catalog.Find("gemini")!.NeedsKey);
    }

    [Fact]
    public void EveryBuiltInAddress_PassesTheSameChecksAsTheUserAddress()
    {
        foreach (var p in ProviderCatalog.LoadEmbedded().All.Where(p => !p.IsCustom))
            Assert.Null(AssistantEndpoint.Validate(p.BaseUrl, out _));
    }

    [Fact]
    public void Find_IsCaseInsensitive_AndUnknownIsNull()
    {
        var catalog = ProviderCatalog.LoadEmbedded();
        Assert.NotNull(catalog.Find("GEMINI"));
        Assert.Null(catalog.Find("nope"));
        Assert.Null(catalog.Find(null));
    }

    [Fact]
    public void BrokenEntries_AreSkipped_NotFatal()
    {
        const string json = """
            [
              { "id": "ok", "name": "Ok", "baseUrl": "https://example.com/v1/", "needsKey": true, "defaultModel": "", "keyUrl": "", "freeTier": "", "badge": "" },
              { "id": "ok", "name": "Duplicate", "baseUrl": "https://example.com/v1/", "needsKey": true, "defaultModel": "", "keyUrl": "", "freeTier": "", "badge": "" },
              { "id": "", "name": "No id", "baseUrl": "https://example.com/v1/", "needsKey": true, "defaultModel": "", "keyUrl": "", "freeTier": "", "badge": "" },
              { "id": "insecure", "name": "Insecure", "baseUrl": "http://example.com/v1/", "needsKey": true, "defaultModel": "", "keyUrl": "", "freeTier": "", "badge": "" },
              { "id": "mine", "name": "Mine", "baseUrl": "", "needsKey": false, "defaultModel": "", "keyUrl": "", "freeTier": "", "badge": "" }
            ]
            """;
        var catalog = ProviderCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(new[] { "ok", "mine" }, catalog.All.Select(p => p.Id));
        Assert.True(catalog.Find("mine")!.IsCustom);
    }
}

public class AssistantErrorTests
{
    [Theory]
    [InlineData(401, AssistantErrorKind.InvalidKey)]
    [InlineData(403, AssistantErrorKind.InvalidKey)]
    [InlineData(404, AssistantErrorKind.ModelNotFound)]
    [InlineData(429, AssistantErrorKind.RateLimited)]
    [InlineData(400, AssistantErrorKind.BadRequest)]
    [InlineData(422, AssistantErrorKind.BadRequest)]
    [InlineData(503, AssistantErrorKind.ProviderUnavailable)]
    [InlineData(500, AssistantErrorKind.ProviderUnavailable)]
    [InlineData(504, AssistantErrorKind.Timeout)]
    [InlineData(418, AssistantErrorKind.Unknown)]
    public void Status_MapsToAKindAndAPortugueseMessage(int status, AssistantErrorKind kind)
    {
        var ex = AssistantErrors.FromStatus(status, null);
        Assert.Equal(kind, ex.Kind);
        Assert.Equal(status, ex.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void ProviderDetail_IsAppended_FlattenedAndShortened()
    {
        var ex = AssistantErrors.FromStatus(429, "quota\n  exceeded\r\n" + new string('x', 500));
        Assert.DoesNotContain('\n', ex.Message);
        Assert.Contains("quota exceeded", ex.Message);
        Assert.True(ex.Message.Length < 450);
    }

    [Theory]
    [InlineData("Please pass a valid API key")]
    [InlineData("API key not valid. Please pass a valid API key.")]
    [InlineData("Invalid api_key provided")]
    public void A400ThatBlamesTheKey_IsAnInvalidKey_NotABadRequest(string detail)
    {
        // Gemini says 400 (not 401) for a key it does not accept; sending the user to "choose another model" would mislead.
        var ex = AssistantErrors.FromStatus(400, detail);
        Assert.Equal(AssistantErrorKind.InvalidKey, ex.Kind);
        Assert.Equal(AssistantErrorKind.BadRequest, AssistantErrors.FromStatus(400, "tools is not supported").Kind);
    }

    [Fact]
    public void Ollama404_ExplainsHowToPullTheModel()
    {
        Assert.Contains("ollama pull", AssistantErrors.FromStatus(404, null, localProvider: true).Message);
        Assert.DoesNotContain("ollama pull", AssistantErrors.FromStatus(404, null, localProvider: false).Message);
    }

    [Fact]
    public void Network_ForALocalProvider_SaysToStartOllama()
    {
        Assert.Contains("Ollama", AssistantErrors.Network(localProvider: true).Message);
        Assert.Equal(AssistantErrorKind.Network, AssistantErrors.Network(false).Kind);
    }

    [Fact]
    public void Redact_RemovesTheKey_ButLeavesShortStringsAlone()
    {
        Assert.Equal("bad key *** for you", AssistantErrors.Redact("bad key AIza-secret-key for you", "AIza-secret-key"));
        Assert.Equal("a b a", AssistantErrors.Redact("a b a", "a"));   // too short to be a key: do not mangle the text
        Assert.Equal("text", AssistantErrors.Redact("text", null));
    }
}
