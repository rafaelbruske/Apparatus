using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Apparatus.Core;
using Apparatus.Core.Actions;
using Apparatus.Core.Input;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Apparatus.Native;

/// <summary>The window that has the focus, for step conditions.</summary>
public sealed unsafe class ForegroundProbe : IForegroundProbe
{
    public ForegroundInfo Get()
    {
        nint hwnd = Interop.GetForegroundWindow();
        if (hwnd == 0) return new ForegroundInfo("", "");
        char* buffer = stackalloc char[256];
        int n = Interop.GetWindowTextW(hwnd, buffer, 256);
        return new ForegroundInfo(KeyTranslator.ForegroundProcessName(), n > 0 ? new string(buffer, 0, n) : "");
    }
}

/// <summary>Sends key combinations and media keys to the focused window.</summary>
public static unsafe class KeySender
{
    private static readonly Dictionary<string, ushort> MediaKeys = new()
    {
        ["play-pause"] = 0xB3, ["next"] = 0xB0, ["previous"] = 0xB1, ["stop"] = 0xB2,
    };

    public static void Send(IReadOnlyList<KeyCombo> sequence)
    {
        // The user may still be holding the hotkey that started the action; wait so Ctrl/Alt do not leak in.
        TextInjector.WaitForModifiersReleased(800);
        foreach (var combo in sequence)
        {
            var inputs = new List<INPUT>();
            var held = new List<ushort>();
            if (combo.Modifiers.HasFlag(HotkeyModifiers.Win)) held.Add(0x5B);
            if (combo.Modifiers.HasFlag(HotkeyModifiers.Control)) held.Add(0x11);
            if (combo.Modifiers.HasFlag(HotkeyModifiers.Alt)) held.Add(0x12);
            if (combo.Modifiers.HasFlag(HotkeyModifiers.Shift)) held.Add(0x10);

            foreach (var m in held) inputs.Add(Key(m, up: false));
            inputs.Add(Key((ushort)combo.VirtualKey, up: false));
            inputs.Add(Key((ushort)combo.VirtualKey, up: true));
            for (int i = held.Count - 1; i >= 0; i--) inputs.Add(Key(held[i], up: true));

            Deliver(inputs.ToArray());
            Thread.Sleep(15);
        }
    }

    public static void Media(string key)
    {
        if (!MediaKeys.TryGetValue(key, out ushort vk)) throw new ArgumentException($"Tecla de mídia desconhecida: {key}");
        Deliver(new[] { Key(vk, up: false), Key(vk, up: true) });
    }

    private static void Deliver(INPUT[] inputs)
    {
        uint sent = PInvoke.SendInput(inputs, sizeof(INPUT));
        if (sent != inputs.Length) throw new InvalidOperationException("O Windows recusou o envio das teclas (a janela ativa pode estar em modo administrador).");
    }

    private static INPUT Key(ushort vk, bool up)
    {
        var input = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        input.Anonymous.ki.wVk = (VIRTUAL_KEY)vk;
        input.Anonymous.ki.wScan = (ushort)PInvoke.MapVirtualKey(vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC);
        input.Anonymous.ki.dwExtraInfo = KeyboardHook.OwnMarker;
        var flags = up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0;
        // Navigation keys, the keypad's "/" and the right-hand variants are "extended" keys.
        if (vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x6F or >= 0xA6 and <= 0xB7) flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY;
        input.Anonymous.ki.dwFlags = flags;
        return input;
    }
}

public static class SystemCommands
{
    public static void Run(string command)
    {
        switch (command)
        {
            case "lock":
                if (!Interop.LockWorkStation()) throw new Win32Exception();
                break;
            case "sleep":
                Interop.SetSuspendState(false, false, false);
                break;
            case "monitor-off":
                Interop.PostMessageW(Interop.HWND_BROADCAST, Interop.WM_SYSCOMMAND, 0xF170 /* SC_MONITORPOWER */, 2);
                break;
            case "show-desktop":
                KeySender.Send(new[] { new KeyCombo(HotkeyModifiers.Win, 0x44 /* D */) });
                break;
            case "empty-recycle-bin":
                Interop.SHEmptyRecycleBinW(0, null, 0);   // flags 0: Windows asks for confirmation itself
                break;
            case "screensaver":
                Interop.PostMessageW(Interop.GetForegroundWindow(), Interop.WM_SYSCOMMAND, 0xF140 /* SC_SCREENSAVE */, 0);
                break;
            default:
                throw new ArgumentException($"Comando do sistema desconhecido: {command}");
        }
    }
}

/// <summary>Starts programs and scripts, optionally waiting for them and reporting failure.</summary>
public static class ProcessRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public static async Task RunAsync(ProcessStartInfo psi, bool wait, bool captureOutput, CancellationToken ct)
    {
        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (captureOutput)
        {
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) AppLog.Info($"[script] {e.Data}"); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null && stderr.Length < 4000) stderr.AppendLine(e.Data); };
        }

        try { process.Start(); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("O pedido de elevação (UAC) foi cancelado."); }
        catch (Win32Exception ex) { throw new InvalidOperationException($"Não foi possível iniciar “{psi.FileName}”: {ex.Message}"); }

        if (captureOutput)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        if (!wait) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"“{psi.FileName}” passou de {Timeout.TotalMinutes:0} minutos e foi encerrado.");
        }

        if (captureOutput && process.ExitCode != 0)
        {
            string first = stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            throw new InvalidOperationException($"O comando terminou com código {process.ExitCode}" + (first.Length > 0 ? $": {first}" : "."));
        }
    }
}

/// <summary>Runs every step that only needs Windows itself (no UI of ours).</summary>
public sealed class WindowsStepExecutor : IStepExecutor
{
    private static readonly HashSet<StepKind> Handled = new()
    {
        StepKind.OpenApp, StepKind.OpenUrl, StepKind.OpenPath, StepKind.RunCommand, StepKind.RunPowerShell,
        StepKind.SendKeys, StepKind.Window, StepKind.Volume, StepKind.Brightness, StepKind.Media, StepKind.System,
    };

    public bool CanExecute(StepKind kind) => Handled.Contains(kind);

    public Task ExecuteAsync(ActionStep step, CancellationToken ct) => step.Kind switch
    {
        StepKind.OpenApp => OpenApp(step, ct),
        StepKind.OpenUrl or StepKind.OpenPath => Task.Run(() => ShellLauncher.Open(step.Get(step.Kind == StepKind.OpenUrl ? "url" : "path")), ct),
        StepKind.RunCommand => RunCommand(step, ct),
        StepKind.RunPowerShell => RunPowerShell(step, ct),
        StepKind.SendKeys => Task.Run(() =>
        {
            KeyCombo.TryParseSequence(step.Get("keys"), out var sequence);
            KeySender.Send(sequence);
        }, ct),
        StepKind.Window => Task.Run(() => WindowManager.Apply(step.Get("command")), ct),
        StepKind.Volume => Task.Run(() => Volume(step), ct),
        StepKind.Brightness => Task.Run(() => Brightness(step), ct),
        StepKind.Media => Task.Run(() => KeySender.Media(step.Get("key")), ct),
        StepKind.System => Task.Run(() => SystemCommands.Run(step.Get("command")), ct),
        _ => throw new NotSupportedException(step.Kind.ToString()),
    };

    private static Task OpenApp(ActionStep step, CancellationToken ct)
    {
        string target = step.Get("target").Trim();
        var psi = new ProcessStartInfo(target) { UseShellExecute = true };
        if (step.Get("args").Length > 0) psi.Arguments = step.Get("args");
        if (step.GetBool("admin")) psi.Verb = "runas";
        if (Path.IsPathRooted(target) && File.Exists(target)) psi.WorkingDirectory = Path.GetDirectoryName(target)!;
        return ProcessRunner.RunAsync(psi, wait: false, captureOutput: false, ct);
    }

    private static Task RunCommand(ActionStep step, CancellationToken ct)
    {
        bool hidden = step.GetBool("hidden", true);
        var psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{step.Get("command")}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = hidden,
            RedirectStandardOutput = hidden,
            RedirectStandardError = hidden,
        };
        return ProcessRunner.RunAsync(psi, step.GetBool("wait", true), captureOutput: hidden, ct);
    }

    private static async Task RunPowerShell(ActionStep step, CancellationToken ct)
    {
        bool hidden = step.GetBool("hidden", true);
        string script = step.Get("script");
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        // A command line holds ~32k characters; longer scripts go through a temporary file.
        string? file = null;
        string arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass ";
        if (encoded.Length > 7000)
        {
            file = Path.Combine(Path.GetTempPath(), $"apparatus-{Guid.NewGuid():N}.ps1");
            await File.WriteAllTextAsync(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct).ConfigureAwait(false);
            arguments += $"-File \"{file}\"";
        }
        else
        {
            arguments += $"-EncodedCommand {encoded}";
        }

        var psi = new ProcessStartInfo("powershell.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = hidden,
            RedirectStandardOutput = hidden,
            RedirectStandardError = hidden,
        };
        try
        {
            await ProcessRunner.RunAsync(psi, step.GetBool("wait", true), captureOutput: hidden, ct).ConfigureAwait(false);
        }
        finally
        {
            // Only safe to delete once the script has finished reading it, i.e. when we waited for it.
            if (file is not null && step.GetBool("wait", true)) { try { File.Delete(file); } catch (IOException) { } }
        }
    }

    private static void Volume(ActionStep step)
    {
        int amount = step.GetInt("amount", 5);
        switch (step.Get("mode"))
        {
            case "up": AudioControl.ChangeVolume(amount); break;
            case "down": AudioControl.ChangeVolume(-amount); break;
            case "set": AudioControl.SetVolume(amount); break;
            case "mute": AudioControl.ToggleMute(); break;
            default: throw new ArgumentException($"Modo de volume desconhecido: {step.Get("mode")}");
        }
    }

    private static void Brightness(ActionStep step)
    {
        int amount = step.GetInt("amount", 10);
        switch (step.Get("mode"))
        {
            case "up": BrightnessControl.Change(amount); break;
            case "down": BrightnessControl.Change(-amount); break;
            case "set": BrightnessControl.Set(amount); break;
            default: throw new ArgumentException($"Modo de brilho desconhecido: {step.Get("mode")}");
        }
    }
}
