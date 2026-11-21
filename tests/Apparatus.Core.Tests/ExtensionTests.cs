using System.Text.Json;
using Apparatus.Core.Extensions;
using Apparatus.Core.Search;

namespace Apparatus.Core.Tests;

public class ExtensionManifestTests
{
    private static ExtensionManifest Valid() => new()
    {
        Id = "my-ext", Name = "Minha extensão", Command = "powershell.exe", Keyword = "me",
    };

    [Fact]
    public void AValidManifest_Passes() => Valid().Validate();

    [Theory]
    [InlineData("", "id")]
    [InlineData("A", "id")]
    [InlineData("Has Space", "id")]
    [InlineData("UPPER", "id")]
    public void BadIds_AreRejected(string id, string messagePart)
    {
        var m = Valid();
        m.Id = id;
        Assert.Contains(messagePart, Assert.Throws<ExtensionManifestException>(() => m.Validate()).Message);
    }

    [Fact]
    public void RequiredFields_AreChecked()
    {
        var noName = Valid(); noName.Name = " ";
        Assert.Contains("name", Assert.Throws<ExtensionManifestException>(() => noName.Validate()).Message);
        var noCommand = Valid(); noCommand.Command = "";
        Assert.Contains("command", Assert.Throws<ExtensionManifestException>(() => noCommand.Validate()).Message);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("waytoolongkeyword12345")]
    [InlineData("semi;colon")]
    public void BadKeywords_AreRejected(string keyword)
    {
        var m = Valid(); m.Keyword = keyword;
        Assert.Contains("keyword", Assert.Throws<ExtensionManifestException>(() => m.Validate()).Message);
    }

    [Fact]
    public void ModeAndTimeouts_AreChecked()
    {
        var mode = Valid(); mode.Mode = "daemon";
        Assert.Contains("mode", Assert.Throws<ExtensionManifestException>(() => mode.Validate()).Message);
        var timeout = Valid(); timeout.TimeoutMs = 50;
        Assert.Contains("timeoutMs", Assert.Throws<ExtensionManifestException>(() => timeout.Validate()).Message);
        var debounce = Valid(); debounce.DebounceMs = 99999;
        Assert.Contains("debounceMs", Assert.Throws<ExtensionManifestException>(() => debounce.Validate()).Message);
    }

    [Fact]
    public void Debounce_DefaultsDependOnTheMode()
    {
        var persistent = Valid();
        var oneshot = Valid(); oneshot.Mode = "OneShot";
        Assert.Equal(0, persistent.EffectiveDebounceMs);
        Assert.Equal(250, oneshot.EffectiveDebounceMs);
        oneshot.DebounceMs = 10;
        Assert.Equal(10, oneshot.EffectiveDebounceMs);
    }

    [Fact]
    public void Load_ReadsTheFile_AndResolvesPathsInsideTheFolder()
    {
        using var dir = new TempDir();
        dir.Touch(@"ext\main.ps1");
        File.WriteAllText(Path.Combine(dir.Path, "ext", "extension.json"), """
            { "id": "loaded", "name": "Carregada", "command": "powershell.exe",
              "args": ["-File", "main.ps1", "-Other", "{dir}", "fora.txt"], "keyword": "ld" }
            """);
        var m = ExtensionManifest.Load(Path.Combine(dir.Path, "ext"));

        Assert.Equal("loaded", m.Id);
        var args = m.ResolveArgs();
        Assert.Equal("-File", args[0]);
        Assert.Equal(Path.Combine(m.Folder, "main.ps1"), args[1]);   // exists in the folder: made absolute
        Assert.Equal(m.Folder, args[3]);                              // {dir}
        Assert.Equal("fora.txt", args[4]);                            // does not exist: left alone
        Assert.Equal("powershell.exe", m.ResolveCommand());
    }

    [Theory]
    [InlineData("não é json", "JSON inválido")]
    [InlineData("{}", "id")]
    public void Load_ReportsProblems(string content, string messagePart)
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "extension.json"), content);
        Assert.Contains(messagePart, Assert.Throws<ExtensionManifestException>(() => ExtensionManifest.Load(dir.Path)).Message);
    }

    [Fact]
    public void Messages_RoundTripThroughTheLineFormat()
    {
        var line = new ExtMessage { Type = "query", Id = 7, Query = "olá ação", Keyword = "hello" }.ToLine();
        Assert.DoesNotContain("\n", line);
        var back = ExtMessage.Parse(line)!;
        Assert.Equal("query", back.Type);
        Assert.Equal(7, back.Id);
        Assert.Equal("olá ação", back.Query);
        Assert.DoesNotContain("results", line);   // unset fields are omitted
    }

    [Theory]
    [InlineData("")]
    [InlineData("texto solto")]
    [InlineData("{ quebrado")]
    [InlineData("[1,2]")]
    public void Garbage_IsIgnored(string line) => Assert.Null(ExtMessage.Parse(line));

    [Fact]
    public void ABom_BeforeTheJson_IsTolerated() =>
        Assert.Equal("results", ExtMessage.Parse("﻿{\"type\":\"results\"}")!.Type);

    [Fact]
    public void Results_ParseWithActions()
    {
        var m = ExtMessage.Parse("""{"type":"results","id":3,"results":[{"id":"a","title":"T","score":5,"action":{"type":"open","target":"https://x.com"},"alt":{"type":"copy","text":"c"}}]}""")!;
        var r = Assert.Single(m.Results!);
        Assert.Equal("open", r.Action!.Type);
        Assert.Equal("https://x.com", r.Action.Target);
        Assert.Equal("copy", r.Alt!.Type);
        Assert.Equal(5, r.Score);
    }
}

public class ExtensionHostTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<(string Title, string Message)> _notifications = new();
    private readonly List<ExtensionHost> _hosts = new();

    public void Dispose()
    {
        foreach (var h in _hosts) h.Dispose();
        _dir.Dispose();
    }

    private static string SampleFolder()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Samples", "hello-powershell");
        Assert.True(File.Exists(Path.Combine(path, "main.ps1")), "the sample extension must be copied next to the tests");
        return path;
    }

    private ExtensionHost Host(IEnumerable<string>? enabled = null, IEnumerable<string>? global = null)
    {
        var host = new ExtensionHost(_dir.Path, () => (enabled ?? Array.Empty<string>()).ToList(), () => (global ?? Array.Empty<string>()).ToList(),
            (t, m) => { lock (_notifications) _notifications.Add((t, m)); });
        _hosts.Add(host);
        return host;
    }

    /// <summary>Writes a PowerShell extension whose body is <paramref name="script"/> and returns its manifest folder.</summary>
    private string MakeExtension(string id, string script, string mode = "persistent", int timeoutMs = 3000, string keyword = "t")
    {
        string folder = Path.Combine(_dir.Path, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "main.ps1"), script);
        File.WriteAllText(Path.Combine(folder, "extension.json"), JsonSerializer.Serialize(new
        {
            id, name = id, command = "powershell.exe", keyword, mode, timeoutMs,
            args = new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "main.ps1" },
        }));
        return folder;
    }

    private const string Prelude = """
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        $reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
        $writer = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
        $writer.AutoFlush = $true
        function Send($m) { $writer.WriteLine(($m | ConvertTo-Json -Compress -Depth 8)) }
        """;

    // ---------------------------------------------------------------- tests

    [Fact]
    public void Discovery_FindsValidExtensions_AndReportsBrokenOnes()
    {
        MakeExtension("good", Prelude);
        Directory.CreateDirectory(Path.Combine(_dir.Path, "broken"));
        File.WriteAllText(Path.Combine(_dir.Path, "broken", "extension.json"), "{ \"id\": \"x\" }");
        Directory.CreateDirectory(Path.Combine(_dir.Path, "not-an-extension"));
        MakeExtension("good-copy", Prelude);
        File.WriteAllText(Path.Combine(_dir.Path, "good-copy", "extension.json"), """{"id":"good","name":"dup","command":"x"}""");

        var host = Host();
        Assert.Equal(new[] { "good" }, host.List().Select(e => e.Manifest.Id).ToArray());
        Assert.Equal(2, host.LoadErrors.Count);
        Assert.Contains(host.LoadErrors, e => e.StartsWith("broken:"));
        Assert.Contains(host.LoadErrors, e => e.Contains("já é usado"));
    }

    [Fact]
    public void NothingRunsUntilEnabled()
    {
        MakeExtension("quiet", Prelude);
        var host = Host();
        Assert.False(host.List().Single().Enabled);
        Assert.Empty(host.Enabled());

        var enabled = Host(enabled: new[] { "QUIET" });   // ids compare case-insensitively
        Assert.True(enabled.List().Single().Enabled);
        Assert.False(enabled.List().Single().Global);

        var global = Host(enabled: new[] { "quiet" }, global: new[] { "quiet" });
        Assert.True(global.List().Single().Global);
        Assert.False(Host(global: new[] { "quiet" }).List().Single().Global);   // "global" only counts for enabled extensions
    }

    [Fact]
    public async Task TheSampleExtension_Answers_AndIsActivated()
    {
        CopyDirectory(SampleFolder(), Path.Combine(_dir.Path, "hello-powershell"));
        var host = Host(enabled: new[] { "hello-powershell" });
        var manifest = host.List().Single().Manifest;

        var results = await host.QueryAsync(manifest, "mundo", "hello", CancellationToken.None);
        Assert.Contains(results, r => r.Title == "Olá, mundo!" && r.Action is { Type: "copy", Text: "Olá, mundo!" });
        Assert.Contains(results, r => r.Id == "time" && r.Action!.Type == "insert");
        var temp = results.Single(r => r.Id == "temp");
        Assert.Equal("open", temp.Action!.Type);
        Assert.Equal("copy", temp.Alt!.Type);
        Assert.Equal("callback", results.Single(r => r.Id == "count").Action!.Type);

        // A second request reuses the running process (and so is quick).
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var again = await host.QueryAsync(manifest, "ação", "hello", CancellationToken.None);
        Assert.Contains(again, r => r.Title == "Olá, ação!");   // accents survive the round trip
        Assert.True(clock.ElapsedMilliseconds < 1500, $"second query took {clock.ElapsedMilliseconds} ms");

        var help = await host.QueryAsync(manifest, "", "hello", CancellationToken.None);
        Assert.Equal("none", help.Single().Action!.Type);

        // The "callback" action: the script counts files and answers with a tray notification.
        await host.ActivateAsync("hello-powershell", "count", JsonDocument.Parse("""{"folder":"Downloads"}""").RootElement.Clone(), CancellationToken.None);
        lock (_notifications) Assert.Contains(_notifications, n => n.Title == "Olá, PowerShell" && n.Message.Contains("arquivo"));
        Assert.Equal(ExtensionStatus.Running, host.List().Single().Status);
    }

    [Fact]
    public async Task ASilentExtension_TimesOut_WithoutBlockingForever()
    {
        MakeExtension("mute", Prelude + "\nwhile ($true) { Start-Sleep -Seconds 1 }", timeoutMs: 600);
        var host = Host(enabled: new[] { "mute" });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => host.QueryAsync(host.List().Single().Manifest, "x", null, CancellationToken.None));
        Assert.InRange(clock.ElapsedMilliseconds, 500, 4000);
    }

    [Fact]
    public async Task Cancellation_IsReportedAsCancellation_NotTimeout()
    {
        MakeExtension("slow", Prelude + "\nwhile ($true) { Start-Sleep -Seconds 1 }", timeoutMs: 20000);
        var host = Host(enabled: new[] { "slow" });
        using var cts = new CancellationTokenSource();
        var task = host.QueryAsync(host.List().Single().Manifest, "x", null, cts.Token);
        await Task.Delay(1200);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Noise_OnStdout_IsIgnored_AndRepliesWithoutAnIdStillArrive()
    {
        MakeExtension("noisy", Prelude + """

            Write-Output "isto não é JSON"
            while ($null -ne ($line = $reader.ReadLine())) {
                Write-Output "{ quebrado"
                Send @{ type = 'results'; results = @(@{ id = 'ok'; title = 'Funcionou' }) }
            }
            """);
        var host = Host(enabled: new[] { "noisy" });
        var results = await host.QueryAsync(host.List().Single().Manifest, "x", null, CancellationToken.None);
        Assert.Equal("Funcionou", results.Single().Title);
    }

    [Fact]
    public async Task ACrashingExtension_FailsTheRequest_AndStopsRestartingAfterThreeTries()
    {
        MakeExtension("crashy", "[Console]::Error.WriteLine('algo deu errado'); exit 1", timeoutMs: 5000);
        var host = Host(enabled: new[] { "crashy" });
        var manifest = host.List().Single().Manifest;

        for (int i = 0; i < 3; i++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => host.QueryAsync(manifest, "x", null, CancellationToken.None));
            await Task.Delay(300);
        }
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.QueryAsync(manifest, "x", null, CancellationToken.None));
        Assert.Contains("encerrou", ex.Message);
        var info = host.List().Single();
        Assert.Equal(ExtensionStatus.Failed, info.Status);
        Assert.Contains("algo deu errado", info.Error);

        // Reloading gives it a clean slate.
        host.Reload();
        Assert.Equal(ExtensionStatus.Idle, host.List().Single().Status);
    }

    [Fact]
    public async Task MissingProgram_GivesAClearError()
    {
        string folder = MakeExtension("ghost", Prelude);
        File.WriteAllText(Path.Combine(folder, "extension.json"), """{"id":"ghost","name":"ghost","command":"programa-que-nao-existe.exe"}""");
        var host = Host(enabled: new[] { "ghost" });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.QueryAsync(host.List().Single().Manifest, "x", null, CancellationToken.None));
        Assert.Contains("programa-que-nao-existe.exe", ex.Message);
        Assert.Equal(ExtensionStatus.Failed, host.List().Single().Status);
    }

    [Fact]
    public async Task OneShotExtensions_GetAFreshProcessPerRequest()
    {
        MakeExtension("once", Prelude + """

            $req = $reader.ReadLine() | ConvertFrom-Json
            Send @{ type = 'notify'; title = 'oneshot'; message = 'pedido recebido' }
            Send @{ type = 'results'; id = $req.id; results = @(@{ id = 'r'; title = ('eco: ' + $req.query) }) }
            """, mode: "oneshot", timeoutMs: 5000);
        var host = Host(enabled: new[] { "once" });
        var manifest = host.List().Single().Manifest;

        Assert.Equal("eco: um", (await host.QueryAsync(manifest, "um", null, CancellationToken.None)).Single().Title);
        Assert.Equal("eco: dois", (await host.QueryAsync(manifest, "dois", null, CancellationToken.None)).Single().Title);
        lock (_notifications) Assert.Equal(2, _notifications.Count(n => n.Title == "oneshot"));
    }

    [Fact]
    public async Task Stop_And_Reload_EndTheProcess()
    {
        MakeExtension("echo", Prelude + """

            while ($null -ne ($line = $reader.ReadLine())) {
                $req = $line | ConvertFrom-Json
                Send @{ type = 'results'; id = $req.id; results = @(@{ id = 'p'; title = ('pid ' + $PID) }) }
            }
            """);
        var host = Host(enabled: new[] { "echo" });
        var manifest = host.List().Single().Manifest;

        string first = (await host.QueryAsync(manifest, "a", null, CancellationToken.None)).Single().Title;
        string same = (await host.QueryAsync(manifest, "b", null, CancellationToken.None)).Single().Title;
        Assert.Equal(first, same);                    // one process serves both

        host.Stop("echo");
        string afterStop = (await host.QueryAsync(manifest, "c", null, CancellationToken.None)).Single().Title;
        Assert.NotEqual(first, afterStop);            // a new process after stopping
    }

    // -------------------------------------------------------------- provider

    [Theory]
    [InlineData("hello world", "hello", true, "world")]
    [InlineData("HELLO   world  ", "hello", true, "world  ")]
    [InlineData("hello", "hello", true, "")]
    [InlineData("helloworld", "hello", false, "")]
    [InlineData("say hello", "hello", false, "")]
    [InlineData("hell", "hello", false, "")]
    public void KeywordMatching(string raw, string keyword, bool matches, string remainder)
    {
        Assert.Equal(matches, ExtensionsProvider.TryStripKeyword(raw, keyword, out string rest));
        if (matches) Assert.Equal(remainder, rest);
    }

    [Fact]
    public async Task Provider_AsksTheRightExtensions_AndMapsResults()
    {
        CopyDirectory(SampleFolder(), Path.Combine(_dir.Path, "hello-powershell"));
        MakeExtension("other", Prelude + """

            while ($null -ne ($line = $reader.ReadLine())) {
                $req = $line | ConvertFrom-Json
                Send @{ type = 'results'; id = $req.id; results = @(@{ id = 'g'; title = ('global: ' + $req.query); score = 1 }) }
            }
            """, keyword: "");
        var disabled = MakeExtension("disabled", Prelude);

        var host = Host(enabled: new[] { "hello-powershell", "other" }, global: new[] { "other" });
        var provider = new ExtensionsProvider(host);

        // keyword query: the keyword extension answers (ranked on top); the global one answers too
        var results = await provider.QueryAsync(SearchContext.Create("hello mundo"), CancellationToken.None);
        var greet = results.Single(r => r.Title == "Olá, mundo!");
        Assert.Equal(ResultKind.Extension, greet.Kind);
        Assert.True(greet.Score > 8000);
        Assert.Equal("ext:hello-powershell:greet", greet.Key);
        var payload = ExtPayload.FromJson(greet.Payload)!;
        Assert.Equal("hello-powershell", payload.E);
        Assert.Equal("copy", payload.A!.Type);
        Assert.Contains(results, r => r.Title == "global: hello mundo" && r.Score < 1000);

        // ordinary query: only the global extension is consulted
        var plain = await provider.QueryAsync(SearchContext.Create("calculadora"), CancellationToken.None);
        Assert.All(plain, r => Assert.StartsWith("ext:other:", r.Key));

        // a single character is too little to send to every global extension
        Assert.Empty(await provider.QueryAsync(SearchContext.Create("c"), CancellationToken.None));
    }

    [Fact]
    public async Task Provider_SwallowsFailures_SoTheLauncherKeepsWorking()
    {
        MakeExtension("crashy", "exit 1", keyword: "boom");
        var host = Host(enabled: new[] { "crashy" });
        var results = await new ExtensionsProvider(host).QueryAsync(SearchContext.Create("boom now"), CancellationToken.None);
        Assert.Empty(results);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
    }
}
