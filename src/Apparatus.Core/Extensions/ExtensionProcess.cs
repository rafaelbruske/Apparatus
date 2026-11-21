using System.Diagnostics;
using System.Text;

namespace Apparatus.Core.Extensions;

public enum ExtensionStatus { Idle, Running, Failed }

/// <summary>
/// Talks to one extension program. Persistent extensions keep a single process and answer requests by id;
/// one-shot extensions get a fresh process per request. Every failure (crash, timeout, garbage on stdout)
/// is contained here: the launcher just gets an exception for that one request and carries on.
/// </summary>
internal sealed class ExtensionProcess : IDisposable
{
    private const int MaxRestartsPerMinute = 3;

    private readonly ExtensionManifest _manifest;
    private readonly Action<string, string> _notify;
    private readonly object _gate = new();
    private readonly Dictionary<int, TaskCompletionSource<ExtMessage>> _pending = new();
    private readonly Queue<DateTime> _starts = new();
    private readonly Queue<string> _stderr = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private int _nextId;
    private bool _disposed;

    public ExtensionProcess(ExtensionManifest manifest, Action<string, string> notify)
    {
        _manifest = manifest;
        _notify = notify;
    }

    public ExtensionStatus Status { get; private set; } = ExtensionStatus.Idle;

    /// <summary>The last thing that went wrong, or the tail of the extension's stderr.</summary>
    public string? LastError { get; private set; }

    // ------------------------------------------------------------ requests

    public async Task<IReadOnlyList<ExtResult>> QueryAsync(string query, string? keyword, CancellationToken ct)
    {
        var request = new ExtMessage { Type = "query", Query = query, Keyword = string.IsNullOrEmpty(keyword) ? null : keyword };
        var reply = await RequestAsync(request, "results", ct).ConfigureAwait(false);
        return (IReadOnlyList<ExtResult>?)reply.Results ?? Array.Empty<ExtResult>();
    }

    public Task ActivateAsync(string resultId, System.Text.Json.JsonElement? data, CancellationToken ct) =>
        RequestAsync(new ExtMessage { Type = "activate", Result = resultId, Data = data }, "done", ct);

    private Task<ExtMessage> RequestAsync(ExtMessage request, string expectedReply, CancellationToken ct) =>
        _manifest.IsPersistent ? RequestPersistentAsync(request, expectedReply, ct) : RequestOneShotAsync(request, expectedReply, ct);

    // ---------------------------------------------------------- persistent

    private async Task<ExtMessage> RequestPersistentAsync(ExtMessage request, string expectedReply, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ExtMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int id = Interlocked.Increment(ref _nextId);
        request.Id = id;

        lock (_gate)
        {
            EnsureStarted();
            _pending[id] = tcs;
            try
            {
                _stdin!.WriteLine(request.ToLine());
                _stdin.Flush();
            }
            catch (IOException ex)
            {
                _pending.Remove(id);
                throw new IOException($"A extensão não aceitou o pedido: {ex.Message}", ex);
            }
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_manifest.TimeoutMs);
            using var registration = timeout.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"“{_manifest.Name}” não respondeu em {_manifest.TimeoutMs} ms.");
        }
        finally
        {
            lock (_gate) _pending.Remove(id);
        }
    }

    /// <summary>Must be called with <see cref="_gate"/> held.</summary>
    private void EnsureStarted()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ExtensionProcess));
        if (_process is { HasExited: false }) return;

        var now = DateTime.UtcNow;
        while (_starts.Count > 0 && now - _starts.Peek() > TimeSpan.FromMinutes(1)) _starts.Dequeue();
        if (_starts.Count >= MaxRestartsPerMinute)
        {
            Status = ExtensionStatus.Failed;
            throw new InvalidOperationException($"“{_manifest.Name}” encerrou {MaxRestartsPerMinute} vezes seguidas. Recarregue as extensões depois de corrigi-la.");
        }
        _starts.Enqueue(now);

        _process = StartProcess();
        _stdin = _process.StandardInput;
        var process = _process;
        Status = ExtensionStatus.Running;
        AppLog.Info($"Extension '{_manifest.Id}' started (pid {process.Id})");

        _ = Task.Run(() => ReadStdoutAsync(process));
        _ = Task.Run(() => ReadStderrAsync(process));
    }

    private async Task ReadStdoutAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null) Dispatch(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }

        // The process is gone: nobody will answer what is still waiting.
        lock (_gate)
        {
            if (!ReferenceEquals(_process, process)) return;
            foreach (var tcs in _pending.Values) tcs.TrySetException(new IOException($"“{_manifest.Name}” terminou inesperadamente."));
            _pending.Clear();
            if (Status == ExtensionStatus.Running) Status = ExtensionStatus.Idle;
        }
        AppLog.Info($"Extension '{_manifest.Id}' exited");
    }

    private async Task ReadStderrAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null) RememberStderr(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void RememberStderr(string line)
    {
        if (line.Trim().Length == 0) return;
        AppLog.Info($"[ext {_manifest.Id}] {line}");
        lock (_stderr)
        {
            _stderr.Enqueue(line.Trim());
            while (_stderr.Count > 5) _stderr.Dequeue();
            LastError = string.Join(" | ", _stderr);
        }
    }

    private void Dispatch(string line)
    {
        var message = ExtMessage.Parse(line);
        if (message is null)
        {
            if (line.Trim().Length > 0) AppLog.Info($"[ext {_manifest.Id}] ignored non-JSON output: {Truncate(line)}");
            return;
        }

        switch (message.Type)
        {
            case "notify":
                _notify(message.Title ?? _manifest.Name, message.Message ?? "");
                break;
            case "log":
                AppLog.Info($"[ext {_manifest.Id}] {message.Message}");
                break;
            default:
                lock (_gate)
                {
                    // A reply without an id belongs to the only request in flight, if there is exactly one.
                    int? id = message.Id ?? (_pending.Count == 1 ? _pending.Keys.First() : null);
                    if (id is { } key && _pending.TryGetValue(key, out var tcs)) tcs.TrySetResult(message);
                }
                break;
        }
    }

    // ------------------------------------------------------------- one-shot

    private async Task<ExtMessage> RequestOneShotAsync(ExtMessage request, string expectedReply, CancellationToken ct)
    {
        request.Id = Interlocked.Increment(ref _nextId);
        using var process = StartProcess();
        Status = ExtensionStatus.Running;
        try
        {
            await process.StandardInput.WriteLineAsync(request.ToLine().AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_manifest.TimeoutMs);

            _ = Task.Run(async () =>
            {
                try
                {
                    string? l;
                    while ((l = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null) RememberStderr(l);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            }, CancellationToken.None);

            ExtMessage? reply = null;
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false)) is not null)
            {
                var message = ExtMessage.Parse(line);
                if (message is null) continue;
                if (message.Type == "notify") _notify(message.Title ?? _manifest.Name, message.Message ?? "");
                else if (message.Type == "log") AppLog.Info($"[ext {_manifest.Id}] {message.Message}");
                else reply ??= message;
            }
            return reply ?? new ExtMessage { Type = expectedReply };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"“{_manifest.Name}” não respondeu em {_manifest.TimeoutMs} ms.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            Status = ExtensionStatus.Idle;
        }
    }

    // --------------------------------------------------------------- common

    private Process StartProcess()
    {
        var psi = new ProcessStartInfo(_manifest.ResolveCommand())
        {
            WorkingDirectory = _manifest.Folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in _manifest.ResolveArgs()) psi.ArgumentList.Add(arg);

        try
        {
            return Process.Start(psi) ?? throw new InvalidOperationException("O processo não iniciou.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = ExtensionStatus.Failed;
            LastError = $"Não foi possível iniciar “{_manifest.Command}”: {ex.Message}";
            throw new InvalidOperationException(LastError, ex);
        }
    }

    private static string Truncate(string s) => s.Length <= 120 ? s : s[..120] + "…";

    public void Dispose()
    {
        Process? process;
        lock (_gate)
        {
            _disposed = true;
            process = _process;
            _process = null;
            foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
            _pending.Clear();
        }
        try
        {
            _stdin?.Close();
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
        Status = ExtensionStatus.Idle;
    }
}
