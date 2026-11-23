using Apparatus.Core.Settings;

namespace Apparatus.Core.Assistant;

/// <summary>What the settings say, resolved against the provider catalog: where to connect and with what.</summary>
public sealed record AssistantConnection(ProviderInfo Provider, string BaseUrl, string Model, bool HasKey);

/// <summary>Turns settings + catalog + stored key into a connection or a ready client. The key is read here and goes nowhere else than the client.</summary>
public static class AssistantSetup
{
    /// <summary>The address and model for the chosen provider; the key itself is not read, only whether one exists.</summary>
    public static AssistantConnection? Resolve(AppSettings settings, ProviderCatalog catalog, AssistantKeyStore keys)
    {
        var provider = catalog.Find(settings.AssistantProvider);
        if (provider is null) return null;
        string baseUrl = provider.IsCustom ? settings.AssistantBaseUrl.Trim() : provider.BaseUrl;
        return new AssistantConnection(provider, baseUrl, ModelFor(settings, provider), keys.Has(provider.Id));
    }

    /// <summary>The model the user picked for this provider, else the catalog's default (possibly empty).</summary>
    public static string ModelFor(AppSettings settings, ProviderInfo provider) =>
        settings.AssistantModels.TryGetValue(provider.Id, out var model) && !string.IsNullOrWhiteSpace(model)
            ? model.Trim()
            : provider.DefaultModel;

    /// <summary>Null when the assistant can be used, otherwise what is missing, worded for the user.</summary>
    public static string? WhatIsMissing(AssistantConnection? connection)
    {
        if (connection is null) return "Escolha um provedor.";
        if (connection.Provider.IsCustom && connection.BaseUrl.Length == 0) return "Informe o endereço do servidor.";
        if (AssistantEndpoint.Validate(connection.BaseUrl, out var uri) is { } badAddress) return badAddress;
        if (connection.Provider.NeedsKey && !connection.HasKey) return "Cole a chave de API deste provedor e salve.";
        if (connection.Model.Length == 0)
            return AssistantEndpoint.IsLocal(uri!) ? "Escolha um modelo (clique em Atualizar lista para ver os instalados)." : "Escolha um modelo (clique em Atualizar lista).";
        return null;
    }

    /// <summary>A client for the current settings. Throws <see cref="AssistantException"/> with a message for the user when something is missing.</summary>
    public static IAssistantClient CreateClient(AppSettings settings, ProviderCatalog catalog, AssistantKeyStore keys, TimeSpan? timeout = null)
    {
        var connection = Resolve(settings, catalog, keys);
        if (WhatIsMissing(connection) is { } missing)
        {
            var kind = connection is { Provider.NeedsKey: true, HasKey: false } ? AssistantErrorKind.InvalidKey
                : connection is { Model.Length: 0 } ? AssistantErrorKind.ModelNotFound
                : AssistantErrorKind.InvalidEndpoint;
            throw new AssistantException(kind, missing);
        }
        return ChatClientAdapter.Create(connection!.BaseUrl, keys.Get(connection.Provider.Id), connection.Model, timeout);
    }

    /// <summary>The same, but only the model list is needed, so a missing model is not an error.</summary>
    public static IAssistantClient CreateClientForModelList(AppSettings settings, ProviderCatalog catalog, AssistantKeyStore keys, TimeSpan? timeout = null)
    {
        var connection = Resolve(settings, catalog, keys);
        if (connection is null) throw new AssistantException(AssistantErrorKind.InvalidEndpoint, "Escolha um provedor.");
        // The adapter wants a model to bind to; any placeholder works for listing.
        var withModel = connection.Model.Length > 0 ? connection : connection with { Model = "model" };
        if (WhatIsMissing(withModel) is { } missing)
            throw new AssistantException(connection.Provider.NeedsKey && !connection.HasKey ? AssistantErrorKind.InvalidKey : AssistantErrorKind.InvalidEndpoint, missing);
        return ChatClientAdapter.Create(withModel.BaseUrl, keys.Get(withModel.Provider.Id), withModel.Model, timeout);
    }

    private static readonly string[] NotForChat =
    {
        "embedding", "embed", "aqa", "imagen", "image", "veo", "tts", "audio", "whisper", "transcribe", "realtime",
        "moderation", "guard", "dall-e", "live", "robotics", "computer-use",
    };

    /// <summary>
    /// The models worth offering in the picker: those that are not obviously for something other than chat (embeddings, speech, images).
    /// For Gemini, the fast "flash-lite" models come first (fewer seconds per answer), then "flash", then the rest, newest names first.
    /// The user can still type any model id by hand.
    /// </summary>
    public static IReadOnlyList<string> ModelChoices(string providerId, IEnumerable<string> models)
    {
        var chat = models
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Where(m => !NotForChat.Any(w => m.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal);

        if (providerId.Equals("gemini", StringComparison.OrdinalIgnoreCase))
        {
            return chat
                .Where(m => m.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Contains("flash-lite", StringComparison.OrdinalIgnoreCase) ? 0 : m.Contains("flash", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(m => m.Contains("preview", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenByDescending(m => m, StringComparer.Ordinal)
                .ToList();
        }
        return chat.OrderBy(m => m, StringComparer.Ordinal).ToList();
    }
}
