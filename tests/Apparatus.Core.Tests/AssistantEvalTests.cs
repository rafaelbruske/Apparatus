using System.Text.Json;
using Apparatus.Core.Assistant;
using Apparatus.Core.Settings;
using Xunit.Abstractions;

namespace Apparatus.Core.Tests;

/// <summary>The reference questions used to judge a real provider. Here only the file itself is checked: its expectations must be reachable.</summary>
public class AssistantEvalTests
{
    /// <param name="Alt">Other catalog entries that answer the question just as well (for example the classic control panel page).</param>
    private sealed record Case(string Q, string Tool, string? Uri, string? Screen, IReadOnlyList<string> Alt)
    {
        public IEnumerable<string> AcceptedUris => Uri is null ? Alt : Alt.Prepend(Uri);
    }

    private static List<Case> Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assistant-eval.json")));
        return doc.RootElement.GetProperty("cases").EnumerateArray().Select(e => new Case(
            e.GetProperty("q").GetString()!,
            e.GetProperty("tool").GetString()!,
            e.TryGetProperty("uri", out var u) ? u.GetString() : null,
            e.TryGetProperty("screen", out var s) ? s.GetString() : null,
            e.TryGetProperty("alt", out var a) ? a.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string>())).ToList();
    }

    [Fact]
    public void EveryExpectation_PointsToSomethingTheAssistantCanReallyDo()
    {
        var catalog = SettingsCatalog.LoadEmbedded();
        var tools = new[]
        {
            AssistantTools.OpenWindowsSetting, AssistantTools.OpenApparatusScreen, AssistantTools.OpenLauncher, AssistantTools.OpenUrl,
            AssistantTools.RunMyAction, AssistantTools.ProposeAction, AssistantTools.ProposeSnippet, "none",
        };
        var cases = Load();
        Assert.True(cases.Count >= 20);
        Assert.Equal(cases.Count, cases.Select(c => c.Q).Distinct().Count());

        foreach (var c in cases)
        {
            Assert.Contains(c.Tool, tools);
            if (c.Tool == AssistantTools.OpenWindowsSetting)
                foreach (var uri in c.AcceptedUris)
                    Assert.True(catalog.Find(uri) is not null, $"“{c.Q}”: o catálogo não tem {uri}");
            if (c.Tool == AssistantTools.OpenApparatusScreen)
                Assert.Contains(AssistantTools.Screens, s => s.Id == c.Screen);
        }
    }

    // ------------------------------------------------------------------------------------------ real provider (opt-in)

    private readonly ITestOutputHelper _output;

    public AssistantEvalTests(ITestOutputHelper output) => _output = output;

    private static SuggestionKind? KindOf(string tool) => tool switch
    {
        AssistantTools.OpenWindowsSetting => SuggestionKind.OpenWindowsSetting,
        AssistantTools.OpenApparatusScreen => SuggestionKind.OpenApparatusScreen,
        AssistantTools.OpenLauncher => SuggestionKind.OpenLauncher,
        AssistantTools.OpenUrl => SuggestionKind.OpenUrl,
        AssistantTools.RunMyAction => SuggestionKind.RunMyAction,
        AssistantTools.ProposeAction => SuggestionKind.ProposeAction,
        AssistantTools.ProposeSnippet => SuggestionKind.ProposeSnippet,
        _ => null,   // "none": no button expected
    };

    /// <summary>True when what the assistant offered satisfies the case. Any one of the offered buttons may be the right one.</summary>
    private static bool Matches(Case c, IReadOnlyList<SuggestedAction> offered)
    {
        if (KindOf(c.Tool) is not { } kind) return offered.Count == 0;
        return offered.Any(a => a.Kind == kind
            && (c.Uri is null || c.AcceptedUris.Any(u => string.Equals(a.Payload, u, StringComparison.OrdinalIgnoreCase)))
            && (c.Screen is null || string.Equals(a.Payload, c.Screen, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Asks the reference questions of assistant-eval.json to a REAL provider, through the same engine the launcher uses, and reports which ones
    /// ended in the expected button. Runs only when a key is in the environment; otherwise it returns at once (so CI and
    /// everyone without a key are unaffected). Nothing runs on the machine: the engine only produces suggestions.
    ///
    ///   APPARATUS_TEST_EVAL_PROVIDER provider id from assistant-providers.json (default "gemini"; e.g. "groq")
    ///   APPARATUS_TEST_&lt;ID&gt;_KEY      key for that provider, e.g. APPARATUS_TEST_GEMINI_KEY or APPARATUS_TEST_GROQ_KEY
    ///   APPARATUS_TEST_EVAL_KEY      alternative: one key, whatever the provider
    ///   APPARATUS_TEST_EVAL_MODEL    model to use (default: the provider's default)
    ///   APPARATUS_TEST_EVAL_MIN      lowest accepted hit rate, 0 to 1 (default 0.7)
    ///
    /// The report is written to the test output and to assistant-eval-report.txt next to the test assembly. The key is never printed.
    /// </summary>
    [Fact]
    public async Task Integration_RealProvider_AnswersTheReferenceQuestions_WhenAKeyIsInTheEnvironment()
    {
        string provider = Environment.GetEnvironmentVariable("APPARATUS_TEST_EVAL_PROVIDER") is { Length: > 0 } p ? p : "gemini";
        string? key = Environment.GetEnvironmentVariable("APPARATUS_TEST_EVAL_KEY");
        if (string.IsNullOrWhiteSpace(key))   // APPARATUS_TEST_<PROVIDER>_KEY, e.g. APPARATUS_TEST_GEMINI_KEY or APPARATUS_TEST_GROQ_KEY
            key = Environment.GetEnvironmentVariable($"APPARATUS_TEST_{provider.ToUpperInvariant().Replace('-', '_')}_KEY");
        if (string.IsNullOrWhiteSpace(key)) return;

        double min = double.TryParse(Environment.GetEnvironmentVariable("APPARATUS_TEST_EVAL_MIN"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 0.7;

        using var dir = new TempFolder();
        var providers = ProviderCatalog.LoadEmbedded();
        var keys = new AssistantKeyStore(dir.File("assistant.keys"), new FakeKeyProtector());
        keys.Set(provider, key);
        var settings = new AppSettings { AssistantEnabled = true, AssistantProvider = provider };
        if (Environment.GetEnvironmentVariable("APPARATUS_TEST_EVAL_MODEL") is { Length: > 0 } model) settings.AssistantModels[provider] = model;

        var resolved = AssistantSetup.Resolve(settings, providers, keys) ?? throw new InvalidOperationException($"Provedor desconhecido: {provider}");
        var catalog = SettingsCatalog.LoadEmbedded();
        var tools = new AssistantTools(catalog, new AssistantToolOptions());   // no user actions shared: a clean machine
        var prompt = new SystemPromptBuilder(catalog, "eval", "26100");
        using var overall = new CancellationTokenSource(TimeSpan.FromMinutes(15));

        var lines = new List<string> { $"Provedor: {resolved.Provider.Name} · modelo: {(resolved.Model.Length > 0 ? resolved.Model : "(padrão)")} · {DateTime.Now:yyyy-MM-dd HH:mm}", "" };
        int hits = 0, errors = 0;
        var cases = Load();

        foreach (var c in cases)
        {
            var offered = new List<SuggestedAction>();
            var rejected = new List<string>();
            var notes = new List<string>();
            var answer = new System.Text.StringBuilder();
            string? failure = null;

            for (int attempt = 0; attempt < 4 && !overall.IsCancellationRequested; attempt++)
            {
                offered.Clear(); rejected.Clear(); notes.Clear(); answer.Clear(); failure = null;
                try
                {
                    // A new engine and session per case: the questions must not influence one another.
                    var engine = new AssistantEngine(AssistantSetup.CreateClient(settings, providers, keys, TimeSpan.FromSeconds(90)), prompt, tools, new AssistantSession());
                    await foreach (var ev in engine.AskAsync(c.Q, overall.Token))
                    {
                        switch (ev)
                        {
                            case AnswerText t: answer.Append(t.Delta); break;
                            case SuggestionOffered s: offered.Add(s.Action); break;
                            case SuggestionRejected r: rejected.Add($"{r.ToolName}: {r.Reason}"); break;
                            case AssistantNotice n: notes.Add(n.Text); break;
                        }
                    }
                    break;
                }
                catch (AssistantException ex) when (ex.Kind is AssistantErrorKind.RateLimited or AssistantErrorKind.ProviderUnavailable && attempt < 3)
                {
                    failure = ex.Message;
                    await Task.Delay(TimeSpan.FromSeconds(20 * (attempt + 1)), overall.Token);   // free plans: wait for the quota window
                }
                catch (AssistantException ex) when (ex.Kind is AssistantErrorKind.InvalidKey or AssistantErrorKind.ModelNotFound or AssistantErrorKind.InvalidEndpoint)
                {
                    // Every other question would fail the same way: stop now instead of printing the same error for every question.
                    Assert.Fail($"O provedor {resolved.Provider.Name} recusou o pedido já na primeira pergunta ({ex.Kind}): {ex.Message}{Environment.NewLine}" +
                                "Nenhuma pergunta chegou ao modelo. Confira a chave (sem aspas nem espaços extras) e o modelo, e rode de novo.");
                }
                catch (AssistantException ex)
                {
                    failure = ex.Message;
                    break;
                }
            }

            bool ok = failure is null && Matches(c, offered);
            if (ok) hits++;
            if (failure is not null) errors++;

            string got = offered.Count == 0 ? "nenhum botão" : string.Join(", ", offered.Select(a => $"{a.Kind}({a.Payload})"));
            string? detail = c.Uri ?? c.Screen;
            string want = detail is null ? c.Tool : $"{c.Tool}({detail})";
            lines.Add($"{(ok ? "OK  " : failure is not null ? "ERRO" : "FALHA")}  “{c.Q}”");
            if (!ok) lines.Add($"        esperado: {want}   obtido: {(failure is not null ? "erro: " + failure : got)}");
            foreach (var r in rejected) lines.Add($"        descartado pelo Apparatus: {r}");
            foreach (var n in notes) lines.Add($"        aviso: {n}");
            if (!ok && answer.Length > 0) lines.Add($"        texto: {Shorten(answer.ToString())}");

            await Task.Delay(TimeSpan.FromSeconds(2), overall.Token);   // be gentle with per-minute limits
        }

        double rate = cases.Count == 0 ? 0 : (double)hits / cases.Count;
        lines.Insert(2, $"Acertos: {hits}/{cases.Count} ({rate:P0}) · erros de conexão/limite: {errors} · mínimo exigido: {min:P0}");
        string report = string.Join(Environment.NewLine, lines);
        _output.WriteLine(report);
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "assistant-eval-report.txt"), report); } catch { /* the output above is enough */ }

        Assert.DoesNotContain(key, report);
        Assert.True(rate >= min, $"Só {hits}/{cases.Count} perguntas terminaram no botão esperado.{Environment.NewLine}{report}");
    }

    private static string Shorten(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= 140 ? text : text[..140] + "…";
    }
}
