using Apparatus.Core.Search;
using Apparatus.Core.Search.Providers;

namespace Apparatus.Core.Start;

/// <summary>What a brand-new Start menu contains: the programs used most, completed with the usual suspects.</summary>
public static class StartSeed
{
    // Display names of the same program in the languages Windows is commonly installed in.
    private static readonly string[][] WellKnown =
    {
        new[] { "Microsoft Edge" },
        new[] { "Explorador de Arquivos", "File Explorer", "Explorador de Ficheiros" },
        new[] { "Configurações", "Settings" },
        new[] { "Calculadora", "Calculator" },
        new[] { "Bloco de Notas", "Notepad" },
        new[] { "Terminal", "Windows Terminal" },
        new[] { "Paint" },
        new[] { "Microsoft Store", "Loja Microsoft" },
        new[] { "Gerenciador de Tarefas", "Task Manager" },
        new[] { "Ferramenta de Captura", "Snipping Tool" },
        new[] { "Fotos", "Photos" },
        new[] { "Relógio", "Clock" },
    };

    public static SearchResult AppResult(AppEntry app) =>
        new($"app:{app.Id}", ResultKind.App, app.Name, "Aplicativo", app.ParsingName, 0, $"shell:{app.ParsingName}");

    /// <summary>Most-used apps first, then the well-known ones that are installed, up to <paramref name="max"/>.</summary>
    public static IReadOnlyList<SearchResult> PinnedApps(IReadOnlyList<AppEntry> installed, IEnumerable<SearchResult> mostUsed, int max = 12)
    {
        var picked = new List<SearchResult>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Add(SearchResult r) => picked.Count < max && keys.Add(r.Key) && Push(r);
        bool Push(SearchResult r) { picked.Add(r with { Score = 0, Highlight = null, Favorite = false }); return true; }

        foreach (var used in mostUsed.Where(r => r.Kind == ResultKind.App)) Add(used);

        foreach (var names in WellKnown)
        {
            var app = installed.FirstOrDefault(a => names.Any(n => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase)));
            if (app is not null) Add(AppResult(app));
        }
        return picked;
    }

    /// <summary>The small shortcuts of the bottom bar.</summary>
    public static IReadOnlyList<SearchResult> QuickLinks(string documents, string downloads, string pictures)
    {
        var list = new List<SearchResult>
        {
            new("setting:ms-settings:", ResultKind.Setting, "Configurações", "Configurações do Windows", "ms-settings:"),
        };
        // The folders' own shell icons tell Documents, Downloads and Pictures apart.
        void Folder(string path, string title)
        {
            if (path.Length > 0) list.Add(new($"folder:{path}", ResultKind.Folder, title, path, path, 0, $"path:{path}"));
        }
        Folder(documents, "Documentos");
        Folder(downloads, "Downloads");
        Folder(pictures, "Imagens");
        return list;
    }
}
