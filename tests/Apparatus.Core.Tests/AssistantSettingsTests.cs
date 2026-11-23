using System.Text;
using System.Text.Json;
using Apparatus.Core.Assistant;
using Apparatus.Core.Backup;
using Apparatus.Core.Settings;

namespace Apparatus.Core.Tests;

public class AssistantSettingsTests
{
    private const string SecretKey = "AIzaSy-THIS-MUST-NEVER-LEAK-0123456789";

    [Fact]
    public void Defaults_AreOff_AndPointAtGemini()
    {
        var s = new AppSettings();
        Assert.False(s.AssistantEnabled);
        Assert.Equal("gemini", s.AssistantProvider);
        Assert.False(s.AssistantShareActionNames);
        Assert.Empty(s.AssistantModels);
        Assert.Equal("", s.AssistantBaseUrl);
    }

    [Fact]
    public void AppSettings_HasNoPropertyThatCouldHoldASecret()
    {
        // A structural guard: if someone adds "AssistantApiKey" to settings.json (which is backed up and synced in clear), this fails.
        var names = typeof(AppSettings).GetProperties().Select(p => p.Name).ToList();
        string[] forbidden = { "ApiKey", "Secret", "Token", "Password", "Credential" };
        Assert.DoesNotContain(names, n => forbidden.Any(f => n.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Settings_RoundTripThroughJson_WithTheModelsDictionary()
    {
        var s = new AppSettings { AssistantEnabled = true, AssistantProvider = "groq", AssistantShareActionNames = true };
        s.AssistantModels["groq"] = "openai/gpt-oss-120b";
        s.AssistantModels["gemini"] = "gemini-3.5-flash-lite";

        var copy = s.Clone();
        Assert.True(copy.AssistantEnabled);
        Assert.Equal("groq", copy.AssistantProvider);
        Assert.Equal("openai/gpt-oss-120b", copy.AssistantModels["groq"]);
        Assert.Equal(2, copy.AssistantModels.Count);
        Assert.NotSame(s.AssistantModels, copy.AssistantModels);
    }

    [Fact]
    public void SettingsFile_NeverContainsTheKey_EvenWhenAKeyIsStored()
    {
        using var dir = new TempFolder();
        var keys = new AssistantKeyStore(dir.File("assistant.keys"), new FakeKeyProtector());
        var settings = new SettingsStore(dir.File("settings.json"));

        var s = settings.Current.Clone();
        s.AssistantEnabled = true;
        s.AssistantProvider = "gemini";
        s.AssistantModels["gemini"] = "gemini-3.5-flash-lite";
        settings.Update(s);
        keys.Set("gemini", SecretKey);

        string json = File.ReadAllText(dir.File("settings.json"));
        Assert.DoesNotContain(SecretKey, json);
        Assert.DoesNotContain("AIza", json);
        Assert.Contains("gemini-3.5-flash-lite", json);
    }

    [Fact]
    public void Backup_NeverCarriesTheKey_NorTheSwitch_NorTheServerAddress()
    {
        using var dir = new TempFolder();
        using var home = new Machine("CASA");
        new AssistantKeyStore(dir.File("assistant.keys"), new FakeKeyProtector()).Set("gemini", SecretKey);
        var s = home.Settings.Current.Clone();
        s.AssistantEnabled = true;
        s.AssistantProvider = "custom";
        s.AssistantBaseUrl = "http://192.168.0.5:8080/v1/";
        s.AssistantModels["custom"] = "llama";
        s.AssistantShareActionNames = true;
        home.Settings.Update(s);

        byte[] file = home.Export();
        using var ms = new MemoryStream(file);
        var content = BackupBundle.Read(ms);
        var exported = JsonSerializer.Deserialize<AppSettings>(content.SettingsJson!, SettingsStore.JsonOptions)!;

        Assert.DoesNotContain(SecretKey, content.SettingsJson!);
        Assert.False(exported.AssistantEnabled);
        Assert.Equal("", exported.AssistantBaseUrl);
        // The provider choice, model and the privacy preference are ordinary preferences and do travel.
        Assert.Equal("custom", exported.AssistantProvider);
        Assert.Equal("llama", exported.AssistantModels["custom"]);
        Assert.True(exported.AssistantShareActionNames);
        Assert.DoesNotContain(SecretKey, Encoding.UTF8.GetString(file));
    }

    [Fact]
    public void Import_KeepsThisComputersSwitch_AndServerAddress()
    {
        using var home = new Machine("CASA");
        using var work = new Machine("TRABALHO");
        var h = home.Settings.Current.Clone();
        h.AssistantEnabled = true;
        h.AssistantProvider = "groq";
        h.MaxResults = 9;
        home.Settings.Update(h);

        var w = work.Settings.Current.Clone();
        w.AssistantEnabled = false;
        w.AssistantBaseUrl = "http://localhost:1234/v1/";
        work.Settings.Update(w);

        work.Import(home.Export());

        var s = work.Settings.Current;
        Assert.False(s.AssistantEnabled);                              // no key here yet: it must not switch itself on
        Assert.Equal("http://localhost:1234/v1/", s.AssistantBaseUrl);
        Assert.Equal("groq", s.AssistantProvider);                     // preferences do arrive
        Assert.Equal(9, s.MaxResults);
    }
}

public class AssistantSetupTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private readonly ProviderCatalog _catalog = ProviderCatalog.LoadEmbedded();

    public void Dispose() => _dir.Dispose();

    private AssistantKeyStore Keys() => new(_dir.File("assistant.keys"), new FakeKeyProtector());

    [Fact]
    public void Resolve_UsesTheCatalogAddress_AndTheDefaultModel()
    {
        var c = AssistantSetup.Resolve(new AppSettings { AssistantProvider = "gemini" }, _catalog, Keys())!;
        Assert.Equal("gemini", c.Provider.Id);
        Assert.StartsWith("https://generativelanguage.googleapis.com", c.BaseUrl);
        Assert.Equal(_catalog.Find("gemini")!.DefaultModel, c.Model);
        Assert.False(c.HasKey);
    }

    [Fact]
    public void Resolve_PrefersTheUsersModel_AndIgnoresABlankOne()
    {
        var s = new AppSettings { AssistantProvider = "gemini" };
        s.AssistantModels["gemini"] = "  my-model ";
        Assert.Equal("my-model", AssistantSetup.Resolve(s, _catalog, Keys())!.Model);
        s.AssistantModels["gemini"] = "   ";
        Assert.Equal(_catalog.Find("gemini")!.DefaultModel, AssistantSetup.Resolve(s, _catalog, Keys())!.Model);
    }

    [Fact]
    public void Resolve_CustomProvider_UsesTheUsersAddress()
    {
        var c = AssistantSetup.Resolve(new AppSettings { AssistantProvider = "custom", AssistantBaseUrl = " http://localhost:1234/v1/ " }, _catalog, Keys())!;
        Assert.Equal("http://localhost:1234/v1/", c.BaseUrl);
    }

    [Fact]
    public void Resolve_UnknownProvider_IsNull()
    {
        Assert.Null(AssistantSetup.Resolve(new AppSettings { AssistantProvider = "nope" }, _catalog, Keys()));
        Assert.NotNull(AssistantSetup.WhatIsMissing(null));
    }

    [Fact]
    public void WhatIsMissing_SaysWhatToDoNext()
    {
        var keys = Keys();
        var s = new AppSettings { AssistantProvider = "gemini" };

        Assert.Contains("chave", AssistantSetup.WhatIsMissing(AssistantSetup.Resolve(s, _catalog, keys))!);
        keys.Set("gemini", "key-123456");
        Assert.Null(AssistantSetup.WhatIsMissing(AssistantSetup.Resolve(s, _catalog, keys)));

        s.AssistantProvider = "ollama";   // no key needed, but no default model either
        Assert.Contains("modelo", AssistantSetup.WhatIsMissing(AssistantSetup.Resolve(s, _catalog, keys))!);
        s.AssistantModels["ollama"] = "llama3.1";
        Assert.Null(AssistantSetup.WhatIsMissing(AssistantSetup.Resolve(s, _catalog, keys)));

        s.AssistantProvider = "custom";
        Assert.Contains("endereço", AssistantSetup.WhatIsMissing(AssistantSetup.Resolve(s, _catalog, keys))!);
        s.AssistantBaseUrl = "http://example.com/v1";   // remote and not HTTPS
        Assert.Contains("https", AssistantSetup.WhatIsMissing(AssistantSetup.Resolve(s, _catalog, keys))!);
    }

    [Fact]
    public void CreateClient_WithoutAKey_FailsWithAMessageForTheUser_AndNoNetworkCall()
    {
        var ex = Assert.Throws<AssistantException>(() => AssistantSetup.CreateClient(new AppSettings { AssistantProvider = "gemini" }, _catalog, Keys()));
        Assert.Equal(AssistantErrorKind.InvalidKey, ex.Kind);
        Assert.Contains("chave", ex.Message);
    }

    [Fact]
    public void CreateClient_WithEverything_Works()
    {
        var keys = Keys();
        keys.Set("gemini", "key-123456");
        Assert.NotNull(AssistantSetup.CreateClient(new AppSettings { AssistantProvider = "gemini" }, _catalog, keys));
    }

    [Fact]
    public void CreateClientForModelList_DoesNotNeedAModel_ButStillNeedsTheKey()
    {
        var keys = Keys();
        var s = new AppSettings { AssistantProvider = "ollama" };   // no model chosen yet: that is why the user lists them
        Assert.NotNull(AssistantSetup.CreateClientForModelList(s, _catalog, keys));

        s.AssistantProvider = "openai";
        var ex = Assert.Throws<AssistantException>(() => AssistantSetup.CreateClientForModelList(s, _catalog, keys));
        Assert.Equal(AssistantErrorKind.InvalidKey, ex.Kind);
    }

    [Fact]
    public void ModelChoices_Gemini_PutsTheFastLiteModelsFirst_AndDropsWhatIsNotChat()
    {
        var ids = new[]
        {
            "gemini-2.5-pro", "gemini-3.5-flash", "gemini-3.5-flash-lite", "gemini-2.5-flash-lite", "gemini-3.8-flash-preview",
            "gemini-embedding-001", "gemini-2.5-flash-image", "gemini-2.5-flash-preview-tts", "imagen-4.0", "gemma-3-27b-it", "veo-3",
        };
        var choices = AssistantSetup.ModelChoices("gemini", ids);

        Assert.Equal(new[] { "gemini-3.5-flash-lite", "gemini-2.5-flash-lite", "gemini-3.5-flash", "gemini-3.8-flash-preview", "gemini-2.5-pro" }, choices);
    }

    [Fact]
    public void ModelChoices_OtherProviders_AreSortedAndFiltered_AndKeepTheOddOnes()
    {
        var choices = AssistantSetup.ModelChoices("groq", new[] { "whisper-large-v3", "openai/gpt-oss-120b", "llama-guard-4", "llama-3.3-70b-versatile", "", "openai/gpt-oss-120b" });
        Assert.Equal(new[] { "llama-3.3-70b-versatile", "openai/gpt-oss-120b" }, choices);
        Assert.Empty(AssistantSetup.ModelChoices("ollama", Array.Empty<string>()));
    }

    /// <summary>
    /// Runs only when APPARATUS_TEST_GEMINI_KEY is set (a real, free Gemini key); otherwise it returns at once.
    /// It exercises the SDK, the address and the error translation against the real service.
    /// </summary>
    [Fact]
    public async Task Integration_RealGemini_ListsModelsAndAnswers_WhenAKeyIsInTheEnvironment()
    {
        string? key = Environment.GetEnvironmentVariable("APPARATUS_TEST_GEMINI_KEY");
        if (string.IsNullOrWhiteSpace(key)) return;

        var keys = Keys();
        keys.Set("gemini", key);
        var settings = new AppSettings { AssistantProvider = "gemini" };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var models = await AssistantSetup.CreateClientForModelList(settings, _catalog, keys).ListModelsAsync(cts.Token);
        Assert.NotEmpty(models);

        await AssistantSetup.CreateClient(settings, _catalog, keys).TestAsync(cts.Token);

        var bad = new AssistantKeyStore(_dir.File("bad.keys"), new FakeKeyProtector());
        bad.Set("gemini", "not-a-valid-key-0000");
        var ex = await Assert.ThrowsAsync<AssistantException>(() => AssistantSetup.CreateClient(settings, _catalog, bad).TestAsync(cts.Token));
        Assert.True(ex.Kind is AssistantErrorKind.InvalidKey or AssistantErrorKind.BadRequest, ex.Kind.ToString());
        Assert.DoesNotContain("not-a-valid-key-0000", ex.Message);
    }
}
