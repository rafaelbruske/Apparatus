namespace Apparatus.Core.Assistant;

public enum AssistantErrorKind
{
    /// <summary>Anything we could not classify.</summary>
    Unknown,
    /// <summary>No key saved, or the provider refused it (401/403).</summary>
    InvalidKey,
    /// <summary>The model name does not exist for this provider (404).</summary>
    ModelNotFound,
    /// <summary>Rate limit or free-tier quota (429).</summary>
    RateLimited,
    /// <summary>The provider is overloaded or down (5xx).</summary>
    ProviderUnavailable,
    /// <summary>The request was refused as malformed (400/422); often "this model has no tool support".</summary>
    BadRequest,
    /// <summary>No connection, DNS failure, or a local server (Ollama) that is not running.</summary>
    Network,
    /// <summary>No answer within the time limit.</summary>
    Timeout,
    /// <summary>The configured address is not acceptable (not HTTPS and not local).</summary>
    InvalidEndpoint,
}

/// <summary>An error from the provider, already worded for the user (<see cref="Exception.Message"/> is Portuguese).</summary>
public sealed class AssistantException : Exception
{
    public AssistantException(AssistantErrorKind kind, string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public AssistantErrorKind Kind { get; }

    public int? StatusCode { get; }
}

public static class AssistantErrors
{
    /// <summary>The message shown to the user for an HTTP status. <paramref name="detail"/> is the provider's own text (already redacted), appended when present.</summary>
    public static AssistantException FromStatus(int status, string? detail, bool localProvider = false, Exception? inner = null)
    {
        // Some providers (Gemini) answer 400, not 401, for a key they do not accept.
        if (status is 400 or 422 && detail is not null && (detail.Contains("api key", StringComparison.OrdinalIgnoreCase) || detail.Contains("api_key", StringComparison.OrdinalIgnoreCase)))
            status = 401;

        var (kind, text) = status switch
        {
            401 or 403 => (AssistantErrorKind.InvalidKey, "O provedor recusou a chave de API. Confira se ela está correta, ativa e liberada para este modelo."),
            404 => (AssistantErrorKind.ModelNotFound, localProvider
                ? "Modelo não encontrado. No Ollama, baixe-o antes (ollama pull <modelo>) ou escolha outro da lista."
                : "Modelo não encontrado neste provedor. Atualize a lista de modelos e escolha outro."),
            429 => (AssistantErrorKind.RateLimited, "Limite de uso do provedor atingido (comum no plano gratuito). Espere um pouco ou escolha outro modelo."),
            400 or 422 => (AssistantErrorKind.BadRequest, "O provedor recusou o pedido. O modelo escolhido pode não aceitar este tipo de requisição."),
            408 or 504 => (AssistantErrorKind.Timeout, "O provedor demorou demais para responder."),
            >= 500 => (AssistantErrorKind.ProviderUnavailable, "O provedor está sobrecarregado ou fora do ar. Tente de novo em instantes."),
            _ => (AssistantErrorKind.Unknown, $"O provedor respondeu com erro {status}."),
        };
        return new AssistantException(kind, Append(text, detail), status, inner);
    }

    public static AssistantException Network(bool localProvider, Exception? inner = null) =>
        new(AssistantErrorKind.Network,
            localProvider
                ? "Não consegui falar com o servidor local. Se for o Ollama, inicie-o e tente de novo."
                : "Sem conexão com o provedor. Verifique a internet e o endereço configurado.",
            null, inner);

    public static AssistantException Timeout(TimeSpan limit, Exception? inner = null) =>
        new(AssistantErrorKind.Timeout, $"O provedor não respondeu em {limit.TotalSeconds:0} segundos.", null, inner);

    public static AssistantException MissingKey() =>
        new(AssistantErrorKind.InvalidKey, "Nenhuma chave de API salva para este provedor. Cole a chave nas configurações do assistente.");

    /// <summary>Removes every occurrence of the key from text that may be shown or logged.</summary>
    public static string Redact(string text, string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < 6) return text;
        return text.Replace(secret, "***", StringComparison.Ordinal);
    }

    /// <summary>Keeps the provider's explanation short enough for one line of the UI and free of line breaks.</summary>
    public static string? Tidy(string? detail, int max = 240)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;
        string flat = string.Join(' ', detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }

    private static string Append(string text, string? detail) =>
        Tidy(detail) is { } d ? $"{text} ({d})" : text;
}
