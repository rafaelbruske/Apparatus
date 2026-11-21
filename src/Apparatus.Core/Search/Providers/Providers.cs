using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apparatus.Core.Calc;
using Apparatus.Core.Indexing;
using Apparatus.Core.Settings;

namespace Apparatus.Core.Search.Providers;

// ============================================================== apps

public sealed record AppEntry(string Id, string Name, string ParsingName);

/// <summary>Source of installed applications (implemented on Windows via shell:AppsFolder).</summary>
public interface IAppCatalog
{
    Task<IReadOnlyList<AppEntry>> LoadAsync(CancellationToken ct);
}

public sealed class AppsProvider : ISearchProvider
{
    private const int KindBonus = 90;
    private volatile AppEntry[] _apps = Array.Empty<AppEntry>();

    public string Id => "apps";

    public int Count => _apps.Length;

    public void SetApps(IEnumerable<AppEntry> apps) =>
        _apps = apps.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchContext context, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        foreach (var app in _apps)
        {
            int s = FuzzyMatcher.Score(app.Name, context.Fuzzy);
            if (s == FuzzyMatcher.NoMatch) continue;
            results.Add(new SearchResult($"app:{app.Id}", ResultKind.App, app.Name, "Aplicativo", app.ParsingName,
                s + KindBonus, $"shell:{app.ParsingName}"));
        }
        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }
}

// ============================================================= files

public sealed class FilesProvider : ISearchProvider
{
    private readonly FileIndex _index;
    private readonly Func<AppSettings> _settings;

    public FilesProvider(FileIndex index, Func<AppSettings> settings)
    {
        _index = index;
        _settings = settings;
    }

    public string Id => "files";

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchContext context, CancellationToken ct)
    {
        // One-letter queries match nearly everything; keep them to apps and commands.
        if (!_settings().IndexFiles || context.Fuzzy.TotalLength < 2) return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());

        return Task.Run<IReadOnlyList<SearchResult>>(() =>
        {
            var hits = _index.Search(context.Fuzzy, 24, ct);
            return hits.Select(h =>
            {
                string name = Path.GetFileName(h.Path.TrimEnd('\\'));
                if (name.Length == 0) name = h.Path;
                string parent = Path.GetDirectoryName(h.Path.TrimEnd('\\')) ?? "";
                return new SearchResult(
                    $"{(h.IsDirectory ? "dir" : "file")}:{h.Path}",
                    h.IsDirectory ? ResultKind.Folder : ResultKind.File,
                    name, parent, h.Path,
                    h.Score + (h.IsDirectory ? 6 : 0),
                    $"path:{h.Path}");
            }).ToList();
        }, ct);
    }
}

// ===================================================== windows settings

public sealed class WindowsSettingsProvider : ISearchProvider
{
    private sealed record Page(string Name, string[] Names, string Uri, string Keywords);

    private readonly Page[] _pages = Load();

    public string Id => "windows-settings";

    private static Page[] Load()
    {
        using var stream = typeof(WindowsSettingsProvider).Assembly.GetManifestResourceStream("windows-settings.json")
            ?? throw new InvalidOperationException("windows-settings.json is not embedded");
        var raw = JsonSerializer.Deserialize<List<Entry>>(stream) ?? new();
        return raw.Select(e => new Page(e.N[0], e.N, e.U, e.K ?? "")).ToArray();
    }

    private sealed record Entry(
        [property: System.Text.Json.Serialization.JsonPropertyName("n")] string[] N,
        [property: System.Text.Json.Serialization.JsonPropertyName("u")] string U,
        [property: System.Text.Json.Serialization.JsonPropertyName("k")] string? K);

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchContext context, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        foreach (var page in _pages)
        {
            int best = FuzzyMatcher.NoMatch;
            foreach (var n in page.Names) best = Math.Max(best, FuzzyMatcher.Score(n, context.Fuzzy));
            int kw = FuzzyMatcher.Score(page.Keywords, context.Fuzzy);
            if (kw != FuzzyMatcher.NoMatch) best = Math.Max(best, kw - 25);
            if (best == FuzzyMatcher.NoMatch) continue;
            results.Add(new SearchResult($"setting:{page.Uri}", ResultKind.Setting, page.Name,
                page.Uri.StartsWith("ms-settings:", StringComparison.Ordinal) ? "Configurações do Windows" : "Ferramenta do Windows",
                page.Uri, best + 70));
        }
        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }
}

// ===================================================== calculator/units

public sealed class CalculatorProvider : ISearchProvider
{
    public string Id => "calculator";

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchContext context, CancellationToken ct)
    {
        var list = new List<SearchResult>(1);
        if (UnitConverter.TryConvert(context.Raw, out var conv))
        {
            string text = UnitConverter.Format(conv.Result) + " " + conv.ToUnit;
            list.Add(new SearchResult($"calc:{context.Raw}", ResultKind.Calculator, text, conv.Display + "  ·  Enter copia o resultado", text, 10_000));
        }
        else if (ExpressionEvaluator.TryEvaluate(context.Raw, out double value))
        {
            string text = ExpressionEvaluator.Format(value);
            list.Add(new SearchResult($"calc:{context.Raw}", ResultKind.Calculator, text, $"{context.Raw} =  ·  Enter copia o resultado", text, 10_000));
        }
        return Task.FromResult<IReadOnlyList<SearchResult>>(list);
    }
}

// ================================================================= web

public sealed partial class WebSearchProvider : ISearchProvider
{
    private readonly Func<AppSettings> _settings;

    public WebSearchProvider(Func<AppSettings> settings) => _settings = settings;

    public string Id => "web";

    [GeneratedRegex(@"^(https?://|www\.)\S+$|^[\w-]+(\.[\w-]+)*\.(com|org|net|io|dev|app|ai|co|me|tv|xyz|gov|edu|br|pt|us|uk|info|biz)(/\S*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchContext context, CancellationToken ct)
    {
        var settings = _settings();
        var list = new List<SearchResult>(2);
        string raw = context.Raw;
        if (raw.Length == 0) return Task.FromResult<IReadOnlyList<SearchResult>>(list);

        // "yt lofi" -> search YouTube for "lofi"
        int space = raw.IndexOf(' ');
        if (space > 0)
        {
            string keyword = raw[..space];
            var engine = settings.SearchEngines.FirstOrDefault(e => string.Equals(e.Keyword, keyword, StringComparison.OrdinalIgnoreCase));
            string term = raw[(space + 1)..].Trim();
            if (engine is not null && term.Length > 0)
            {
                list.Add(Build(engine, term, 9_000));
                return Task.FromResult<IReadOnlyList<SearchResult>>(list);
            }
        }

        if (!raw.Contains(' ') && UrlPattern().IsMatch(raw))
        {
            string url = raw.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? raw : "https://" + raw;
            list.Add(new SearchResult($"url:{url}", ResultKind.Url, $"Abrir {raw}", "Endereço da web", url, 5_000));
        }

        var fallback = settings.SearchEngines.FirstOrDefault(e => string.Equals(e.Keyword, settings.DefaultSearchEngine, StringComparison.OrdinalIgnoreCase))
                       ?? settings.SearchEngines.FirstOrDefault()
                       ?? new WebSearchEngine { Keyword = "g", Name = "Google", UrlTemplate = "https://www.google.com/search?q={query}" };
        // Always last: a safety net when nothing else matches.
        list.Add(Build(fallback, raw, -500));
        return Task.FromResult<IReadOnlyList<SearchResult>>(list);
    }

    private static SearchResult Build(WebSearchEngine engine, string term, double score)
    {
        string url = engine.UrlTemplate.Replace("{query}", Uri.EscapeDataString(term));
        return new SearchResult($"web:{engine.Keyword}:{term}", ResultKind.Web, $"Pesquisar \"{term}\"", $"Pesquisar no {engine.Name}", url, score);
    }
}

// ============================================================ commands

public static class InternalCommands
{
    public const string OpenSettings = "apparatus:settings";
    public const string Reindex = "apparatus:reindex";
    public const string ClearHistory = "apparatus:clear-history";
    public const string OpenDataFolder = "apparatus:data-folder";
    public const string Quit = "apparatus:quit";
    public const string ManageSnippets = "apparatus:snippets";
    public const string NewSnippet = "apparatus:snippet-new";
    public const string ManageActions = "apparatus:actions";
    public const string NewAction = "apparatus:action-new";
}

public sealed class InternalCommandsProvider : ISearchProvider
{
    private sealed record Command(string Id, string Title, string Subtitle, string Keywords);

    private static readonly Command[] Commands =
    {
        new(InternalCommands.ManageSnippets, "Gerenciar snippets", "Criar, editar, importar e exportar", "apparatus snippets textos atalhos expansão"),
        new(InternalCommands.NewSnippet, "Novo snippet", "Cria um texto reutilizável", "apparatus snippet criar adicionar texto"),
        new(InternalCommands.ManageActions, "Gerenciar ações e atalhos", "Atalhos de teclado e macros", "apparatus ações atalhos macros hotkeys automação"),
        new(InternalCommands.NewAction, "Nova ação", "Cria um atalho ou macro", "apparatus ação criar atalho macro automação"),
        new(InternalCommands.OpenSettings, "Configurações do Apparatus", "Atalho, indexação e preferências", "apparatus preferências opções settings atalho hotkey"),
        new(InternalCommands.Reindex, "Reindexar arquivos", "Atualiza o índice de arquivos e pastas agora", "apparatus indexar atualizar scan arquivos"),
        new(InternalCommands.ClearHistory, "Limpar histórico do launcher", "Esquece os itens usados com frequência", "apparatus histórico apagar recentes"),
        new(InternalCommands.OpenDataFolder, "Abrir pasta de dados do Apparatus", "Configurações, índice e logs", "apparatus logs dados appdata"),
        new(InternalCommands.Quit, "Sair do Apparatus", "Encerra o aplicativo", "apparatus fechar encerrar quit exit"),
    };

    public string Id => "commands";

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchContext context, CancellationToken ct)
    {
        var list = new List<SearchResult>();
        foreach (var c in Commands)
        {
            int s = FuzzyMatcher.Score(c.Title, context.Fuzzy);
            int k = FuzzyMatcher.Score(c.Keywords, context.Fuzzy);
            if (k != FuzzyMatcher.NoMatch) s = Math.Max(s, k - 25);
            if (s == FuzzyMatcher.NoMatch) continue;
            list.Add(new SearchResult($"cmd:{c.Id}", ResultKind.Command, c.Title, c.Subtitle, c.Id, s + 60));
        }
        return Task.FromResult<IReadOnlyList<SearchResult>>(list);
    }
}
