// Spike da Fase 0 do assistente de IA (docs/ASSISTENTE-IA.md).
//
// Pergunta que ele responde: o Microsoft.Extensions.AI.OpenAI, apontado para o endpoint "compatível com OpenAI" de cada
// provedor, entrega (a) texto em streaming, (b) chamadas de ferramenta (tool calls) em streaming e sem streaming,
// (c) aceita o histórico com o resultado sintético da ferramenta, (d) lista os modelos?
//
// Chaves vêm de variáveis de ambiente (nunca de argumentos nem de arquivo). Nada é gravado em disco.
//   GEMINI_API_KEY, GROQ_API_KEY, OPENROUTER_API_KEY, OPENAI_API_KEY   (Ollama não precisa de chave)
// Modelo: SPIKE_<PROVEDOR>_MODEL (ex.: SPIKE_GEMINI_MODEL); sem isso, escolhe um da lista GET /models.
//
// Uso: dotnet run --project spikes/Spike.Assistant -- [--providers gemini,groq,ollama] [--trace]

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;

var argv = args.ToList();
bool trace = argv.Contains("--trace");
bool listOnly = argv.Contains("--models");   // só lista os modelos de cada provedor (não gasta cota de geração)
string[] only = argv.IndexOf("--providers") is int i and >= 0 && i + 1 < argv.Count
    ? argv[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    : Array.Empty<string>();

var providers = new Provider[]
{
    new("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai/", "GEMINI_API_KEY", PickGemini),
    new("groq", "Groq", "https://api.groq.com/openai/v1/", "GROQ_API_KEY", PickGroq),
    new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1/", "OPENROUTER_API_KEY", ids => ids.Where(x => x.EndsWith(":free")).Take(4)),
    new("openai", "OpenAI", "https://api.openai.com/v1/", "OPENAI_API_KEY", ids => ids.Where(x => x.StartsWith("gpt-") && !x.Contains("audio") && !x.Contains("realtime") && !x.Contains("image")).Take(3)),
    new("ollama", "Ollama (local)", "http://localhost:11434/v1/", null, ids => ids.Take(2)),
};

string catalog = LoadCatalog();
string systemPrompt = $"""
    Você é o assistente integrado de um launcher do Windows chamado Apparatus.
    Responda em português do Brasil, de forma curta.
    Sempre que o pedido do usuário puder ser resolvido abrindo uma tela de configurações do Windows, chame a ferramenta
    open_windows_setting com um dos URIs do catálogo abaixo, e explique em uma ou duas frases o que ele vai encontrar.
    Nunca invente URIs que não estejam no catálogo. Nunca diga que executou algo: você só sugere.

    Catálogo de configurações (uri — nome):
    {catalog}
    """;

var tools = new List<AITool>
{
    AIFunctionFactory.CreateDeclaration("open_windows_setting",
        "Sugere abrir uma página de configurações do Windows. O usuário precisa clicar para abrir.",
        Schema("""{"type":"object","properties":{"uri":{"type":"string","description":"URI do catálogo, por exemplo ms-settings:printers"}},"required":["uri"]}""")),
    AIFunctionFactory.CreateDeclaration("open_launcher",
        "Sugere abrir o launcher já com um texto digitado (por exemplo o nome de um aplicativo).",
        Schema("""{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""")),
};

var cases = new Case[]
{
    new("impressora", "Como altero as configurações de impressoras?", "ms-settings:printers"),
    new("brilho/tela", "Quero ajustar a resolução e o brilho da tela.", "ms-settings:display"),
    new("sem ferramenta", "Qual é a capital da França?", null),
};

var rows = new List<Row>();
foreach (var p in providers)
{
    if (only.Length > 0 && !only.Contains(p.Id, StringComparer.OrdinalIgnoreCase)) continue;
    Console.WriteLine();
    Console.WriteLine($"=============== {p.Name} ===============");

    string? key = p.KeyVariable is null ? "ollama" : Environment.GetEnvironmentVariable(p.KeyVariable);
    if (string.IsNullOrWhiteSpace(key))
    {
        Console.WriteLine($"  pulado: defina {p.KeyVariable}");
        rows.Add(new(p.Name, "—", "pulado (sem chave)", "", "", "", ""));
        continue;
    }

    var http = new HttpClient(new TraceHandler(trace) { InnerHandler = new HttpClientHandler() }) { Timeout = TimeSpan.FromSeconds(90) };
    // Sem novas tentativas automáticas: um 429 repetido 4 vezes só queima a cota. O app mostrará o erro ao usuário.
    var options = new OpenAIClientOptions { Endpoint = new Uri(p.BaseUrl), Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0) };
    var client = new OpenAIClient(new ApiKeyCredential(key), options);

    // 1. Lista de modelos (o que a tela de configurações usaria para o combo "Modelo").
    string listResult;
    List<string> ids = new();
    try
    {
        var models = await client.GetOpenAIModelClient().GetModelsAsync();
        ids = models.Value.Select(m => m.Id.StartsWith("models/") ? m.Id["models/".Length..] : m.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        listResult = $"ok ({ids.Count})";
    }
    catch (Exception ex)
    {
        listResult = "FALHOU: " + Short(ex);
    }
    Console.WriteLine($"  GET /models: {listResult}");
    if (listOnly)
    {
        foreach (var id in ids) Console.WriteLine("    " + id);
        continue;
    }

    string? model = Environment.GetEnvironmentVariable($"SPIKE_{p.Id.ToUpperInvariant()}_MODEL");
    if (model is null)
    {
        foreach (var cand in p.Pick(ids))
        {
            await Task.Delay(PauseMs());
            var err = await Probe(client, cand);
            Console.WriteLine($"  teste de {cand}: {(err is null ? "responde" : err)}");
            if (err is null) { model = cand; break; }
        }
    }
    if (model is null)
    {
        Console.WriteLine("  nenhum modelo respondeu. Modelos disponíveis:");
        foreach (var id in ids) Console.WriteLine("    " + id);
        Console.WriteLine("  Defina SPIKE_" + p.Id.ToUpperInvariant() + "_MODEL com um deles.");
        rows.Add(new(p.Name, "?", listResult, "sem modelo (defina SPIKE_" + p.Id.ToUpperInvariant() + "_MODEL)", "", "", ""));
        continue;
    }
    Console.WriteLine($"  modelo: {model}");

    IChatClient chat = client.GetChatClient(model).AsIChatClient();
    var toolOptions = new ChatOptions { Tools = tools, Temperature = 0.2f };

    string stream = "", nonStream = "", followUp = "", noTools = "";
    // 2. Streaming com ferramentas (o caminho real do app).
    foreach (var c in cases)
    {
        var r = await RunStreaming(chat, systemPrompt, c.Question, toolOptions);
        string verdict = Verdict(r, c, catalog);
        stream += (stream.Length > 0 ? "; " : "") + $"{c.Name}: {verdict}";
        Console.WriteLine($"  [stream:{c.Name}] {verdict}  ({r.Elapsed.TotalSeconds:0.0}s)");
        Print(r);

        // 4. Segundo turno: o histórico precisa ser aceito com o resultado sintético da ferramenta.
        if (c.Name == "impressora" && r.Error is null)
        {
            var f = await RunFollowUp(chat, systemPrompt, c.Question, r, toolOptions);
            followUp = f.Error is null ? (f.Text.Length > 0 ? "ok" : "ok (resposta vazia)") : "FALHOU: " + f.Error;
            Console.WriteLine($"  [2º turno] {followUp}");
            Print(f);
        }
    }

    // 3. Sem streaming, uma pergunta (o plano B do app, se o streaming de tool calls falhar).
    {
        var r = await RunNonStreaming(chat, systemPrompt, cases[0].Question, toolOptions);
        nonStream = Verdict(r, cases[0], catalog);
        Console.WriteLine($"  [sem streaming:impressora] {nonStream}  ({r.Elapsed.TotalSeconds:0.0}s)");
        Print(r);
    }

    // 5. Sem ferramentas (modelo que não as suporta).
    {
        var r = await RunStreaming(chat, systemPrompt, cases[2].Question, new ChatOptions { Temperature = 0.2f });
        noTools = r.Error is null ? (r.Text.Length > 0 ? "ok" : "vazio") : "FALHOU: " + r.Error;
        Console.WriteLine($"  [sem ferramentas] {noTools}");
    }

    rows.Add(new(p.Name, model, listResult, stream, nonStream, followUp, noTools));
}

Console.WriteLine();
Console.WriteLine("=============== RESUMO ===============");
foreach (var r in rows)
{
    Console.WriteLine($"{r.Provider}  [{r.Model}]");
    Console.WriteLine($"    /models        : {r.Models}");
    Console.WriteLine($"    streaming+tools: {r.Stream}");
    Console.WriteLine($"    sem streaming  : {r.NonStream}");
    Console.WriteLine($"    2º turno       : {r.FollowUp}");
    Console.WriteLine($"    sem ferramentas: {r.NoTools}");
}
return 0;

// ------------------------------------------------------------------------------------------------ cenários

static async Task<Result> RunStreaming(IChatClient chat, string system, string question, ChatOptions options)
{
    await Task.Delay(PauseMs());
    var sw = Stopwatch.StartNew();
    var updates = new List<ChatResponseUpdate>();
    var text = new StringBuilder();
    try
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, system), new(ChatRole.User, question) };
        await foreach (var u in chat.GetStreamingResponseAsync(messages, options))
        {
            updates.Add(u);
            text.Append(u.Text);
        }
        var response = updates.ToChatResponse();
        return Result.From(response.Messages, text.ToString(), updates.Count, sw.Elapsed, null);
    }
    catch (Exception ex)
    {
        return new(text.ToString(), new(), updates.Count, sw.Elapsed, Short(ex), new());
    }
}

static async Task<Result> RunNonStreaming(IChatClient chat, string system, string question, ChatOptions options)
{
    await Task.Delay(PauseMs());
    var sw = Stopwatch.StartNew();
    try
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, system), new(ChatRole.User, question) };
        var response = await chat.GetResponseAsync(messages, options);
        return Result.From(response.Messages, response.Text, 0, sw.Elapsed, null);
    }
    catch (Exception ex)
    {
        return new("", new(), 0, sw.Elapsed, Short(ex), new());
    }
}

/// <summary>Reenvia o histórico: pergunta, resposta do assistente com a chamada, resultado sintético e uma nova pergunta.</summary>
static async Task<Result> RunFollowUp(IChatClient chat, string system, string question, Result first, ChatOptions options)
{
    await Task.Delay(PauseMs());
    var sw = Stopwatch.StartNew();
    try
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, system), new(ChatRole.User, question) };
        messages.AddRange(first.Messages);   // assistente (texto + FunctionCallContent)
        foreach (var call in first.Calls)
        {
            messages.Add(new ChatMessage(ChatRole.Tool, new AIContent[] { new FunctionResultContent(call.CallId, "Sugerido ao usuário; ainda não executado.") }));
        }
        messages.Add(new ChatMessage(ChatRole.User, "E para configurar o som, o que eu faço?"));

        var updates = new List<ChatResponseUpdate>();
        var text = new StringBuilder();
        await foreach (var u in chat.GetStreamingResponseAsync(messages, options))
        {
            updates.Add(u);
            text.Append(u.Text);
        }
        return Result.From(updates.ToChatResponse().Messages, text.ToString(), updates.Count, sw.Elapsed, null);
    }
    catch (Exception ex)
    {
        return new("", new(), 0, sw.Elapsed, Short(ex), new());
    }
}

// ------------------------------------------------------------------------------------------------ apoio

static int PauseMs() => int.TryParse(Environment.GetEnvironmentVariable("SPIKE_DELAY_MS"), out var d) ? d : 4000;

static string Verdict(Result r, Case c, string catalog)
{
    if (r.Error is not null) return "FALHOU: " + r.Error;
    var setting = r.Calls.FirstOrDefault(x => x.Name == "open_windows_setting");
    if (c.ExpectedUri is null)
        return r.Calls.Count == 0 ? (r.Text.Length > 0 ? "ok (só texto)" : "vazio") : $"ok, mas chamou ferramenta ({r.Calls[0].Name})";
    if (setting is null) return r.Calls.Count == 0 ? "SEM tool call" : $"tool errada ({r.Calls[0].Name})";
    string uri = setting.Args.GetValueOrDefault("uri", "");
    bool inCatalog = catalog.Contains(uri + " —", StringComparison.Ordinal);
    string id = string.IsNullOrEmpty(setting.CallId) ? ", SEM call id" : "";
    string args = setting.ArgsOk ? "" : ", ARGS ILEGÍVEIS";
    if (uri == c.ExpectedUri) return $"ok ({uri}{id}{args})";
    return inCatalog ? $"ok? {uri} (no catálogo, esperado {c.ExpectedUri}{id}{args})" : $"URI FORA DO CATÁLOGO: {uri}{id}{args}";
}

static void Print(Result r)
{
    if (r.Text.Length > 0) Console.WriteLine("      texto: " + r.Text.Replace("\n", " ").Trim()[..Math.Min(220, r.Text.Replace("\n", " ").Trim().Length)]);
    foreach (var c in r.Calls) Console.WriteLine($"      tool : {c.Name}({string.Join(", ", c.Args.Select(a => $"{a.Key}={a.Value}"))})  id={(string.IsNullOrEmpty(c.CallId) ? "(vazio)" : c.CallId)}");
    if (r.Updates > 0) Console.WriteLine($"      {r.Updates} atualizações de streaming");
}

static string Short(Exception ex)
{
    string m = ex.Message.Replace("\r", " ").Replace("\n", " ");
    // Mensagens de erro de provedores às vezes repetem trechos da requisição; nunca devem conter a chave, mas por garantia:
    return (ex.GetType().Name + ": " + (m.Length > 300 ? m[..300] + "…" : m));
}

static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

static string LoadCatalog()
{
    var path = Path.Combine(AppContext.BaseDirectory, "windows-settings.json");
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var sb = new StringBuilder();
    foreach (var e in doc.RootElement.EnumerateArray())
    {
        string name = e.GetProperty("n")[0].GetString() ?? "";
        sb.Append(e.GetProperty("u").GetString()).Append(" — ").Append(name).Append('\n');
    }
    return sb.ToString().TrimEnd();
}

// Candidatos em ordem de preferência; o spike testa cada um com uma chamada mínima e fica com o primeiro que responde.
static IEnumerable<string> PickGemini(List<string> ids)
{
    string[] preferred = { "gemini-3.8-flash", "gemini-3.5-flash-lite", "gemini-2.5-flash", "gemini-flash-latest", "gemini-2.0-flash", "gemini-2.5-flash-lite", "gemini-flash-lite-latest", "gemini-2.0-flash-lite" };
    var bad = new[] { "image", "tts", "live", "exp", "thinking", "audio", "robotics", "computer", "omni", "embedding", "aqa", "imagen", "veo" };
    var rest = ids.Where(x => x.StartsWith("gemini-") && (x.Contains("flash") || x.Contains("pro")) && !bad.Any(x.Contains))
                  .OrderBy(x => x.Contains("preview") ? 1 : 0).ThenByDescending(x => x, StringComparer.Ordinal);
    return preferred.Where(ids.Contains).Concat(rest).Distinct().Take(8);
}

static IEnumerable<string> PickGroq(List<string> ids)
{
    string[] preferred = { "llama-3.3-70b-versatile", "openai/gpt-oss-120b", "openai/gpt-oss-20b", "meta-llama/llama-4-scout-17b-16e-instruct", "qwen/qwen3-32b", "llama-3.1-8b-instant" };
    var bad = new[] { "whisper", "guard", "tts", "orpheus", "playai", "safeguard", "embed" };
    var rest = ids.Where(x => !bad.Any(x.Contains)).OrderBy(x => x, StringComparer.Ordinal);
    return preferred.Where(ids.Contains).Concat(rest).Distinct().Take(8);
}

/// <summary>Chamada mínima só para saber se o modelo responde neste plano (cota, existência).</summary>
static async Task<string?> Probe(OpenAIClient client, string model)
{
    try
    {
        IChatClient c = client.GetChatClient(model).AsIChatClient();
        var r = await c.GetResponseAsync("Responda apenas: ok", new ChatOptions { MaxOutputTokens = 16, Temperature = 0 });
        return null;
    }
    catch (Exception ex) { return Short(ex); }
}

// ------------------------------------------------------------------------------------------------ tipos

record Provider(string Id, string Name, string BaseUrl, string? KeyVariable, Func<List<string>, IEnumerable<string>> Pick);

record Case(string Name, string Question, string? ExpectedUri);

record Call(string Name, string CallId, Dictionary<string, string> Args, bool ArgsOk);

record Result(string Text, List<Call> Calls, int Updates, TimeSpan Elapsed, string? Error, List<ChatMessage> Messages)
{
    public static Result From(IList<ChatMessage> messages, string text, int updates, TimeSpan elapsed, string? error)
    {
        var calls = new List<Call>();
        var kept = new List<ChatMessage>();
        foreach (var m in messages.Where(m => m.Role == ChatRole.Assistant))
        {
            kept.Add(m);
            foreach (var fc in m.Contents.OfType<FunctionCallContent>())
            {
                var args = new Dictionary<string, string>();
                if (fc.Arguments is not null)
                    foreach (var kv in fc.Arguments) args[kv.Key] = kv.Value?.ToString() ?? "";
                calls.Add(new Call(fc.Name, fc.CallId ?? "", args, fc.Exception is null));
            }
        }
        return new(text, calls, updates, elapsed, error, kept);
    }
}

record Row(string Provider, string Model, string Models, string Stream, string NonStream, string FollowUp, string NoTools);

/// <summary>Com --trace, mostra cada requisição (sem o cabeçalho Authorization) e o status da resposta.</summary>
sealed class TraceHandler(bool enabled) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (enabled)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Console.WriteLine($"      >> {request.Method} {request.RequestUri}  {(body.Length > 600 ? body[..600] + "…" : body)}");
        }
        var response = await base.SendAsync(request, ct);
        if (enabled) Console.WriteLine($"      << {(int)response.StatusCode} {response.StatusCode}");
        if (!response.IsSuccessStatusCode)
        {
            // O corpo do erro explica o 429 (limite por minuto, por dia, ou "limit: 0" = modelo fora do plano gratuito).
            string err = await response.Content.ReadAsStringAsync(ct);
            err = err.Replace("\r", " ").Replace("\n", " ");
            Console.WriteLine($"      !! {(int)response.StatusCode} {(err.Length > 700 ? err[..700] + "…" : err)}");
        }
        return response;
    }
}
