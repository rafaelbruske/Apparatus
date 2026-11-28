using Apparatus.Core.Input;

namespace Apparatus.Core.Actions;

public enum FieldType
{
    Text,
    MultiLine,
    /// <summary>An executable or any file (a browse button is shown).</summary>
    File,
    /// <summary>An application: installed ones are suggested while a name is typed; a path works too (with a browse button).</summary>
    App,
    Folder,
    FileOrFolder,
    Number,
    /// <summary>One of <see cref="StepField.Choices"/>.</summary>
    Choice,
    Bool,
    /// <summary>A snippet picked from the user's snippets; the value is the snippet name.</summary>
    Snippet,
}

public sealed record StepField(
    string Key,
    string Label,
    FieldType Type,
    string Default = "",
    IReadOnlyList<(string Value, string Label)>? Choices = null,
    string? Hint = null,
    bool Required = false);

public sealed record StepDescriptor(StepKind Kind, string Title, string Description, string Glyph, IReadOnlyList<StepField> Fields);

/// <summary>
/// Describes every kind of step: its fields (the editor builds its form from this), how it reads in a list
/// and when it is valid. Adding a step kind means adding it here and teaching an executor to run it.
/// </summary>
public static class StepCatalog
{
    private static readonly (string, string)[] WindowCommands =
    {
        ("maximize", "Maximizar"), ("minimize", "Minimizar"), ("restore", "Restaurar"), ("close", "Fechar"),
        ("left-half", "Metade esquerda"), ("right-half", "Metade direita"),
        ("top-half", "Metade superior"), ("bottom-half", "Metade inferior"),
        ("center", "Centralizar"), ("topmost", "Sempre no topo (liga/desliga)"),
        ("next-monitor", "Mover para o próximo monitor"),
    };

    private static readonly (string, string)[] VolumeModes =
        { ("up", "Aumentar"), ("down", "Diminuir"), ("mute", "Silenciar (liga/desliga)"), ("set", "Definir em %") };

    private static readonly (string, string)[] BrightnessModes =
        { ("up", "Aumentar"), ("down", "Diminuir"), ("set", "Definir em %") };

    private static readonly (string, string)[] MediaKeys =
        { ("play-pause", "Tocar / pausar"), ("next", "Próxima faixa"), ("previous", "Faixa anterior"), ("stop", "Parar") };

    private static readonly (string, string)[] SystemCommands =
    {
        ("lock", "Bloquear o computador"), ("sleep", "Suspender"), ("monitor-off", "Desligar o monitor"),
        ("show-desktop", "Mostrar a área de trabalho"), ("empty-recycle-bin", "Esvaziar a lixeira"),
        ("screensaver", "Iniciar a proteção de tela"),
    };

    public static IReadOnlyList<StepDescriptor> All { get; } = new StepDescriptor[]
    {
        new(StepKind.OpenApp, "Abrir aplicativo", "Abre um programa, com argumentos se quiser.", "", new StepField[]
        {
            new("target", "Aplicativo ou arquivo", FieldType.App, Hint: "Digite o nome do aplicativo ou informe o caminho", Required: true),
            new("args", "Argumentos (opcional)", FieldType.Text),
            new("admin", "Executar como administrador", FieldType.Bool, "false"),
        }),
        new(StepKind.OpenUrl, "Abrir site", "Abre um endereço no navegador padrão.", "", new StepField[]
        {
            new("url", "Endereço", FieldType.Text, Hint: "https://exemplo.com", Required: true),
        }),
        new(StepKind.OpenPath, "Abrir arquivo ou pasta", "Abre um documento, ou uma pasta no Explorer.", "", new StepField[]
        {
            new("path", "Caminho", FieldType.FileOrFolder, Required: true),
        }),
        new(StepKind.RunCommand, "Executar comando", "Roda uma linha de comando (cmd).", "", new StepField[]
        {
            new("command", "Comando", FieldType.Text, Hint: "por exemplo: ipconfig /flushdns", Required: true),
            new("hidden", "Ocultar a janela", FieldType.Bool, "true"),
            new("wait", "Aguardar terminar antes do próximo passo", FieldType.Bool, "true"),
        }),
        new(StepKind.RunPowerShell, "Executar PowerShell", "Roda um script do PowerShell.", "", new StepField[]
        {
            new("script", "Script", FieldType.MultiLine, Required: true),
            new("hidden", "Ocultar a janela", FieldType.Bool, "true"),
            new("wait", "Aguardar terminar antes do próximo passo", FieldType.Bool, "true"),
        }),
        new(StepKind.SendKeys, "Enviar teclas", "Pressiona atalhos de teclado no aplicativo ativo.", "", new StepField[]
        {
            new("keys", "Teclas", FieldType.Text, Hint: "Ctrl+C   ou, em sequência:   Ctrl+L Ctrl+V Enter", Required: true),
        }),
        new(StepKind.TypeText, "Digitar texto", "Digita um texto (aceita {date}, {clipboard}…).", "", new StepField[]
        {
            new("text", "Texto", FieldType.MultiLine, Hint: "Aceita as mesmas variáveis dos snippets.", Required: true),
        }),
        new(StepKind.InsertSnippet, "Inserir snippet", "Insere um dos seus snippets.", "", new StepField[]
        {
            new("snippet", "Snippet", FieldType.Snippet, Required: true),
        }),
        new(StepKind.OpenLauncher, "Abrir o launcher", "Abre o launcher já com um texto (por exemplo “clip ” mostra o histórico da área de transferência).", "", new StepField[]
        {
            new("query", "Texto inicial (opcional)", FieldType.Text, Hint: "clip   mostra a área de transferência; hello   chama uma extensão"),
        }),
        new(StepKind.Wait, "Aguardar", "Espera um tempo antes do próximo passo.", "", new StepField[]
        {
            new("ms", "Milissegundos", FieldType.Number, "500", Hint: "1000 = 1 segundo", Required: true),
        }),
        new(StepKind.Window, "Gerenciar janela", "Move ou redimensiona a janela ativa.", "", new StepField[]
        {
            new("command", "O que fazer", FieldType.Choice, "left-half", WindowCommands, Required: true),
        }),
        new(StepKind.Volume, "Volume", "Controla o volume do som.", "", new StepField[]
        {
            new("mode", "Ação", FieldType.Choice, "up", VolumeModes, Required: true),
            new("amount", "Valor (%)", FieldType.Number, "5", Hint: "Passo para aumentar/diminuir; valor final para definir."),
        }),
        new(StepKind.Brightness, "Brilho", "Controla o brilho do monitor.", "", new StepField[]
        {
            new("mode", "Ação", FieldType.Choice, "up", BrightnessModes, Required: true),
            new("amount", "Valor (%)", FieldType.Number, "10", Hint: "Passo para aumentar/diminuir; valor final para definir."),
        }),
        new(StepKind.Media, "Controle de mídia", "Tocar, pausar e trocar de faixa.", "", new StepField[]
        {
            new("key", "Tecla", FieldType.Choice, "play-pause", MediaKeys, Required: true),
        }),
        new(StepKind.System, "Comando do sistema", "Bloquear, suspender e outras.", "", new StepField[]
        {
            new("command", "O que fazer", FieldType.Choice, "lock", SystemCommands, Required: true),
        }),
    };

    public static StepDescriptor Get(StepKind kind) => All.First(d => d.Kind == kind);

    /// <summary>A fresh step of the given kind, with every field at its default.</summary>
    public static ActionStep NewStep(StepKind kind)
    {
        var step = new ActionStep { Kind = kind };
        foreach (var f in Get(kind).Fields)
        {
            if (f.Default.Length > 0) step.Args[f.Key] = f.Default;
        }
        return step;
    }

    // ----------------------------------------------------------- summaries

    public static string Summarize(ActionStep step) => Summarize(step, appName: null);

    /// <param name="appName">
    /// The name of the installed application a target opens, when the caller can tell (see <see cref="AppSuggestions.NameOf"/>). Used
    /// to read a step that was saved without it; nothing is written to the step.
    /// </param>
    public static string Summarize(ActionStep step, Func<string, string?>? appName)
    {
        string text = step.Kind switch
        {
            StepKind.OpenApp => "Abrir " + AppLabel(step, appName) + (step.Get("args").Length > 0 ? " " + step.Get("args") : ""),
            StepKind.OpenUrl => "Abrir " + step.Get("url"),
            StepKind.OpenPath => "Abrir " + Shorten(step.Get("path")),
            StepKind.RunCommand => "Comando: " + FirstLine(step.Get("command")),
            StepKind.RunPowerShell => "PowerShell: " + FirstLine(step.Get("script")),
            StepKind.SendKeys => "Teclas: " + step.Get("keys"),
            StepKind.TypeText => "Digitar: " + FirstLine(step.Get("text")),
            StepKind.InsertSnippet => "Snippet: " + step.Get("snippet"),
            StepKind.OpenLauncher => step.Get("query").Length > 0 ? $"Abrir o launcher com “{step.Get("query")}”" : "Abrir o launcher",
            StepKind.Wait => $"Aguardar {step.Get("ms", "0")} ms",
            StepKind.Window => "Janela: " + ChoiceLabel(StepKind.Window, "command", step.Get("command")),
            StepKind.Volume => VolumeText(step),
            StepKind.Brightness => BrightnessText(step),
            StepKind.Media => "Mídia: " + ChoiceLabel(StepKind.Media, "key", step.Get("key")),
            StepKind.System => ChoiceLabel(StepKind.System, "command", step.Get("command")),
            _ => step.Kind.ToString(),
        };
        return step.HasCondition ? $"{text}  (se {DescribeCondition(step.When!)})" : text;
    }

    public static string DescribeCondition(StepCondition c) => c.Kind switch
    {
        ConditionKind.ActiveProcessIs => $"o app ativo for {c.Value}",
        ConditionKind.ActiveProcessIsNot => $"o app ativo não for {c.Value}",
        ConditionKind.WindowTitleContains => $"o título contiver “{c.Value}”",
        ConditionKind.WindowTitleNotContains => $"o título não contiver “{c.Value}”",
        _ => "",
    };

    private static string VolumeText(ActionStep s) => s.Get("mode") switch
    {
        "up" => $"Volume +{s.Get("amount", "5")}%",
        "down" => $"Volume −{s.Get("amount", "5")}%",
        "mute" => "Silenciar / reativar o som",
        "set" => $"Volume em {s.Get("amount", "50")}%",
        _ => "Volume",
    };

    private static string BrightnessText(ActionStep s) => s.Get("mode") switch
    {
        "up" => $"Brilho +{s.Get("amount", "10")}%",
        "down" => $"Brilho −{s.Get("amount", "10")}%",
        "set" => $"Brilho em {s.Get("amount", "50")}%",
        _ => "Brilho",
    };

    private static string ChoiceLabel(StepKind kind, string fieldKey, string value)
    {
        var field = Get(kind).Fields.FirstOrDefault(f => f.Key == fieldKey);
        var choice = field?.Choices?.FirstOrDefault(c => c.Value == value);
        return choice is { Label: { } label } ? label : value;
    }

    // "name" is kept by the editor when the target is an installed application: it reads better than "shell:AppsFolder\…".
    private static string AppLabel(ActionStep step, Func<string, string?>? appName)
    {
        if (step.Get("name").Length > 0) return step.Get("name");
        string target = step.Get("target");
        return appName?.Invoke(target) is { Length: > 0 } known ? known : Shorten(target);
    }

    private static string FirstLine(string text)
    {
        var line = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        return line.Length <= 60 ? line : line[..59] + "…";
    }

    private static string Shorten(string path)
    {
        if (path.Length <= 48) return path;
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return name.Length > 0 ? "…\\" + name : path[..47] + "…";
    }

    // ---------------------------------------------------------- validation

    /// <summary>Null when the step is complete and well-formed, otherwise a message for the user.</summary>
    public static string? Validate(ActionStep step)
    {
        var descriptor = Get(step.Kind);
        foreach (var field in descriptor.Fields)
        {
            string value = step.Get(field.Key, field.Default).Trim();
            if (field.Required && value.Length == 0) return $"“{descriptor.Title}”: preencha “{field.Label}”.";

            switch (field.Type)
            {
                case FieldType.Number when value.Length > 0 && !int.TryParse(value, out int n):
                    return $"“{descriptor.Title}”: “{field.Label}” precisa ser um número.";
                case FieldType.Choice when field.Choices is not null && value.Length > 0 && field.Choices.All(c => c.Value != value):
                    return $"“{descriptor.Title}”: opção inválida em “{field.Label}”.";
            }
        }

        switch (step.Kind)
        {
            case StepKind.SendKeys when !KeyCombo.TryParseSequence(step.Get("keys"), out _):
                return "“Enviar teclas”: não entendi as teclas. Exemplo: Ctrl+Shift+T, ou em sequência: Ctrl+L Ctrl+V Enter.";
            case StepKind.Wait when step.GetInt("ms") is < 0 or > 600_000:
                return "“Aguardar”: use entre 0 e 600000 ms (10 minutos).";
            case StepKind.Volume or StepKind.Brightness when step.Get("mode") is "set" or "up" or "down" && step.GetInt("amount", -1) is < 0 or > 100:
                return $"“{descriptor.Title}”: o valor deve ficar entre 0 e 100.";
            case StepKind.OpenUrl when !Uri.TryCreate(step.Get("url"), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "mailto"):
                return "“Abrir site”: o endereço deve começar com http://, https:// ou mailto:.";
        }

        if (step.When is { Kind: not ConditionKind.None } cond && cond.Value.Trim().Length == 0)
            return $"“{descriptor.Title}”: preencha o valor da condição.";
        return null;
    }
}
