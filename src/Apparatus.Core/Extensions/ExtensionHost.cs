using System.Text.Json;
using Apparatus.Core.Search;

namespace Apparatus.Core.Extensions;

public sealed record ExtensionInfo(ExtensionManifest Manifest, bool Enabled, bool Global, ExtensionStatus Status, string? Error);

/// <summary>
/// Finds extensions in a folder (one sub-folder each, with an <c>extension.json</c>) and runs the ones the user
/// switched on. Nothing found on disk ever runs by itself: an extension must be enabled explicitly first.
/// </summary>
public sealed class ExtensionHost : IDisposable
{
    private readonly string _folder;
    private readonly Func<IReadOnlyCollection<string>> _enabledIds;
    private readonly Func<IReadOnlyCollection<string>> _globalIds;
    private readonly Action<string, string> _notify;
    private readonly object _gate = new();
    private readonly Dictionary<string, ExtensionProcess> _processes = new(StringComparer.OrdinalIgnoreCase);
    private List<ExtensionManifest> _manifests = new();
    private List<string> _loadErrors = new();

    public ExtensionHost(string folder, Func<IReadOnlyCollection<string>> enabledIds,
        Func<IReadOnlyCollection<string>> globalIds, Action<string, string> notify)
    {
        _folder = folder;
        _enabledIds = enabledIds;
        _globalIds = globalIds;
        _notify = notify;
        Reload();
    }

    public string Folder => _folder;

    /// <summary>Problems found while reading manifests (one line each, naming the folder).</summary>
    public IReadOnlyList<string> LoadErrors
    {
        get { lock (_gate) return _loadErrors; }
    }

    public event EventHandler? Reloaded;

    // ------------------------------------------------------------- discovery

    public void Reload()
    {
        var manifests = new List<ExtensionManifest>();
        var errors = new List<string>();
        if (Directory.Exists(_folder))
        {
            foreach (var dir in Directory.GetDirectories(_folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(Path.Combine(dir, ExtensionManifest.FileName))) continue;
                try
                {
                    var m = ExtensionManifest.Load(dir);
                    if (manifests.Any(x => x.Id.Equals(m.Id, StringComparison.OrdinalIgnoreCase)))
                        throw new ExtensionManifestException($"O id “{m.Id}” já é usado por outra extensão.");
                    manifests.Add(m);
                }
                catch (ExtensionManifestException ex)
                {
                    errors.Add($"{Path.GetFileName(dir)}: {ex.Message}");
                    AppLog.Warn($"Extension in '{dir}' ignored: {ex.Message}");
                }
            }
        }

        List<ExtensionProcess> stale;
        lock (_gate)
        {
            _manifests = manifests;
            _loadErrors = errors;
            stale = _processes.Values.ToList();   // restart everything: files may have changed
            _processes.Clear();
        }
        foreach (var p in stale) p.Dispose();
        Reloaded?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<ExtensionInfo> List()
    {
        var enabled = _enabledIds();
        var global = _globalIds();
        lock (_gate)
        {
            return _manifests.Select(m =>
            {
                _processes.TryGetValue(m.Id, out var p);
                bool on = enabled.Contains(m.Id, StringComparer.OrdinalIgnoreCase);
                return new ExtensionInfo(m, on, on && global.Contains(m.Id, StringComparer.OrdinalIgnoreCase),
                    p?.Status ?? ExtensionStatus.Idle, p?.LastError);
            }).ToList();
        }
    }

    public IReadOnlyList<ExtensionInfo> Enabled() => List().Where(e => e.Enabled).ToList();

    // -------------------------------------------------------------- requests

    private ExtensionProcess ProcessFor(ExtensionManifest manifest)
    {
        lock (_gate)
        {
            if (!_processes.TryGetValue(manifest.Id, out var p))
                _processes[manifest.Id] = p = new ExtensionProcess(manifest, _notify);
            return p;
        }
    }

    public Task<IReadOnlyList<ExtResult>> QueryAsync(ExtensionManifest manifest, string query, string? keyword, CancellationToken ct) =>
        ProcessFor(manifest).QueryAsync(query, keyword, ct);

    public async Task ActivateAsync(string extensionId, string resultId, JsonElement? data, CancellationToken ct)
    {
        ExtensionManifest? manifest;
        lock (_gate) manifest = _manifests.FirstOrDefault(m => m.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase));
        if (manifest is null) throw new InvalidOperationException($"A extensão “{extensionId}” não está mais instalada.");
        await ProcessFor(manifest).ActivateAsync(resultId, data, ct).ConfigureAwait(false);
    }

    /// <summary>Stops one extension's process (when it is switched off).</summary>
    public void Stop(string extensionId)
    {
        ExtensionProcess? p;
        lock (_gate)
        {
            if (!_processes.Remove(extensionId, out p)) return;
        }
        p.Dispose();
    }

    public void Dispose()
    {
        List<ExtensionProcess> all;
        lock (_gate)
        {
            all = _processes.Values.ToList();
            _processes.Clear();
        }
        foreach (var p in all) p.Dispose();
    }
}

/// <summary>Lets the launcher ask enabled extensions, by keyword or (when the user allowed it) for every search.</summary>
public sealed class ExtensionsProvider : ISearchProvider
{
    private const double KeywordScore = 9000;
    private const double GlobalScore = 60;
    private const int MinGlobalQueryLength = 2;

    private readonly ExtensionHost _host;

    public ExtensionsProvider(ExtensionHost host) => _host = host;

    public string Id => "extensions";

    public async Task<IReadOnlyList<SearchResult>> QueryAsync(SearchContext context, CancellationToken ct)
    {
        var jobs = new List<Task<IReadOnlyList<SearchResult>>>();
        foreach (var ext in _host.Enabled())
        {
            var m = ext.Manifest;
            if (m.Keyword.Length > 0 && TryStripKeyword(context.Raw, m.Keyword, out string remainder))
            {
                jobs.Add(Ask(ext, remainder, m.Keyword, KeywordScore, ct));
            }
            else if (ext.Global && context.Raw.Length >= MinGlobalQueryLength)
            {
                jobs.Add(Ask(ext, context.Raw, null, GlobalScore, ct));
            }
        }
        if (jobs.Count == 0) return Array.Empty<SearchResult>();
        return (await Task.WhenAll(jobs).ConfigureAwait(false)).SelectMany(r => r).ToList();
    }

    /// <summary>"hello world" with keyword "hello" gives "world"; "hello" alone gives "".</summary>
    internal static bool TryStripKeyword(string raw, string keyword, out string remainder)
    {
        remainder = "";
        if (raw.Equals(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        if (raw.Length > keyword.Length && raw.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(raw[keyword.Length]))
        {
            remainder = raw[(keyword.Length + 1)..].TrimStart();
            return true;
        }
        return false;
    }

    private async Task<IReadOnlyList<SearchResult>> Ask(ExtensionInfo ext, string query, string? keyword, double baseScore, CancellationToken ct)
    {
        var m = ext.Manifest;
        try
        {
            // One-shot extensions start a program per request: wait for typing to pause first.
            if (m.EffectiveDebounceMs > 0) await Task.Delay(m.EffectiveDebounceMs, ct).ConfigureAwait(false);

            var results = await _host.QueryAsync(m, query, keyword, ct).ConfigureAwait(false);
            return results
                .Where(r => r.Title.Length > 0)
                .Take(20)
                .Select((r, i) => new SearchResult(
                    $"ext:{m.Id}:{(r.Id.Length > 0 ? r.Id : i.ToString())}",
                    ResultKind.Extension,
                    r.Title,
                    string.IsNullOrWhiteSpace(r.Subtitle) ? m.Name : r.Subtitle!,
                    new ExtPayload { E = m.Id, R = r.Id.Length > 0 ? r.Id : i.ToString(), A = r.Action, Alt = r.Alt }.ToJson(),
                    baseScore + (r.Score ?? 0) - i,
                    r.Icon is { } icon && (icon.StartsWith("path:", StringComparison.Ordinal) || icon.StartsWith("shell:", StringComparison.Ordinal)) ? icon : null))
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A broken extension must never break the launcher: say so once in the log and show nothing.
            AppLog.Warn($"Extension '{m.Id}' failed: {ex.Message}");
            return Array.Empty<SearchResult>();
        }
    }
}
