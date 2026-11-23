using System.Diagnostics;
using Apparatus.Core;
using Apparatus.Core.Assistant;
using Apparatus.Native;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Apparatus.App;

/// <summary>One line of the provider picker: the name plus its badge ("Grátis").</summary>
public sealed class AssistantProviderItem
{
    public AssistantProviderItem(ProviderInfo info) => Info = info;

    public ProviderInfo Info { get; }

    public string Label => Info.Badge.Length > 0 ? $"{Info.Name}  ·  {Info.Badge}" : Info.Name;
}

/// <summary>The "Assistente de IA" card of the settings window: provider, key, model, connection test and privacy choices.</summary>
public sealed partial class SettingsWindow
{
    private readonly Dictionary<string, IReadOnlyList<string>> _modelChoices = new();
    private CancellationTokenSource? _assistantCts;
    private bool _assistantLoading;
    private bool _assistantBusy;
    private bool _assistantHooked;

    private ProviderInfo? CurrentAssistantProvider => (AssistantProviderBox.SelectedItem as AssistantProviderItem)?.Info;

    private void LoadAssistant()
    {
        _assistantLoading = true;
        try
        {
            var s = _services.Settings.Current;
            AssistantSwitch.IsOn = s.AssistantEnabled;
            AssistantShareSwitch.IsOn = s.AssistantShareActionNames;
            AssistantKeepSwitch.IsOn = s.AssistantKeepConversation;

            var items = _services.AssistantProviders.All.Select(p => new AssistantProviderItem(p)).ToList();
            AssistantProviderBox.ItemsSource = items;
            AssistantProviderBox.SelectedItem = items.FirstOrDefault(i => i.Info.Id.Equals(s.AssistantProvider, StringComparison.OrdinalIgnoreCase)) ?? items.FirstOrDefault();

            AssistantPromptText.Text =
                $"Instruções extras: crie o arquivo {AppPaths.AssistantPromptFile}. O texto dele é acrescentado às instruções embutidas; se a primeira linha for <!-- replace -->, ele as substitui.";
            RefreshAssistantProvider();
        }
        finally
        {
            _assistantLoading = false;
        }

        if (_assistantHooked) return;
        _assistantHooked = true;
        Closed += (_, _) => _assistantCts?.Cancel();
    }

    /// <summary>Redraws everything that depends on the chosen provider: note, address, key status, model.</summary>
    private void RefreshAssistantProvider()
    {
        var p = CurrentAssistantProvider;
        if (p is null) return;
        var s = _services.Settings.Current;
        bool wasLoading = _assistantLoading;
        _assistantLoading = true;
        try
        {
            AssistantProviderNote.Text = p.FreeTier;

            AssistantUrlBox.Visibility = p.IsCustom ? Visibility.Visible : Visibility.Collapsed;
            AssistantUrlBox.Text = s.AssistantBaseUrl;

            AssistantKeyPanel.Visibility = p.NeedsKey || p.IsCustom ? Visibility.Visible : Visibility.Collapsed;
            AssistantKeyBox.Password = "";
            AssistantSaveKeyButton.IsEnabled = false;
            bool has = _services.AssistantKeys.Has(p.Id);
            AssistantRemoveKeyButton.IsEnabled = has;
            AssistantKeyStatus.Text = has ? "Chave salva neste computador. Para trocar, cole outra e clique em Salvar chave."
                : p.NeedsKey ? "Nenhuma chave salva para este provedor." : "A chave é opcional para este tipo de servidor.";
            if (_services.AssistantKeys.WasReset)
                ShowAssistantResult(InfoBarSeverity.Warning, "As chaves salvas não puderam ser lidas",
                    "Isso acontece quando a conta do Windows ou o perfil mudou. Cole a chave de novo.");

            if (Uri.TryCreate(p.KeyUrl, UriKind.Absolute, out var keyUri) && keyUri.Scheme == Uri.UriSchemeHttps)
            {
                AssistantGetKeyLink.NavigateUri = keyUri;
                AssistantGetKeyLink.Content = p.NeedsKey ? "Obter uma chave" : "Baixar o programa";
                AssistantGetKeyLink.Visibility = Visibility.Visible;
            }
            else
            {
                AssistantGetKeyLink.Visibility = Visibility.Collapsed;
            }

            _modelChoices.TryGetValue(p.Id, out var choices);
            SetAssistantModelChoices(choices ?? Array.Empty<string>(), AssistantSetup.ModelFor(s, p));
        }
        finally
        {
            _assistantLoading = wasLoading;
        }
        RefreshAssistantStatus();
    }

    private void SetAssistantModelChoices(IReadOnlyList<string> choices, string current)
    {
        bool wasLoading = _assistantLoading;
        _assistantLoading = true;
        try
        {
            AssistantModelBox.Text = current;
            AssistantModelList.ItemsSource = choices.ToList();
            AssistantModelList.SelectedItem = choices.FirstOrDefault(c => c == current);
            AssistantModelList.Visibility = choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _assistantLoading = wasLoading;
        }
    }

    private void RefreshAssistantStatus()
    {
        var connection = AssistantSetup.Resolve(_services.Settings.Current, _services.AssistantProviders, _services.AssistantKeys);
        AssistantStatus.Text = AssistantSetup.WhatIsMissing(connection) is { } missing
            ? "Falta: " + missing
            : "Configuração completa. Use “Testar conexão” para confirmar que a chave e o modelo funcionam.";
    }

    private void ShowAssistantResult(InfoBarSeverity severity, string title, string message)
    {
        AssistantResult.Severity = severity;
        AssistantResult.Title = title;
        AssistantResult.Message = message;
        AssistantResult.IsOpen = true;
    }

    // ------------------------------------------------------------------------------------------ choices

    private void OnAssistantToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _assistantLoading) return;
        _services.ApplySettings(s => s.AssistantEnabled = AssistantSwitch.IsOn);
    }

    private void OnAssistantKeepToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _assistantLoading) return;
        _services.ApplySettings(s => s.AssistantKeepConversation = AssistantKeepSwitch.IsOn);
    }

    private void OnAssistantShareToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _assistantLoading) return;
        _services.ApplySettings(s => s.AssistantShareActionNames = AssistantShareSwitch.IsOn);
    }

    private void OnAssistantProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _assistantLoading || CurrentAssistantProvider is not { } p) return;
        AssistantResult.IsOpen = false;
        _services.ApplySettings(s => s.AssistantProvider = p.Id);
        RefreshAssistantProvider();
    }

    private void OnAssistantUrlLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || _assistantLoading) return;
        string value = AssistantUrlBox.Text.Trim();
        if (value == _services.Settings.Current.AssistantBaseUrl) return;
        if (value.Length > 0 && AssistantEndpoint.Validate(value, out _) is { } problem)
        {
            ShowAssistantResult(InfoBarSeverity.Error, "Endereço não aceito", problem);
            return;
        }
        AssistantResult.IsOpen = false;
        _services.ApplySettings(s => s.AssistantBaseUrl = value);
        RefreshAssistantStatus();
    }

    private void OnAssistantModelPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _assistantLoading || AssistantModelList.SelectedItem is not string model) return;
        AssistantModelBox.Text = model;
        SaveAssistantModel(model);
    }

    private void OnAssistantModelKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) SaveAssistantModel(AssistantModelBox.Text);
    }

    private void OnAssistantModelLostFocus(object sender, RoutedEventArgs e) => SaveAssistantModel(AssistantModelBox.Text);

    private void SaveAssistantModel(string? text)
    {
        if (_loading || _assistantLoading || CurrentAssistantProvider is not { } p) return;
        string model = (text ?? "").Trim();
        if (model == AssistantSetup.ModelFor(_services.Settings.Current, p)) return;
        _services.ApplySettings(s =>
        {
            if (model.Length == 0) s.AssistantModels.Remove(p.Id);
            else s.AssistantModels[p.Id] = model;
        });
        RefreshAssistantStatus();
    }

    // ------------------------------------------------------------------------------------------ key

    private void OnAssistantKeyChanged(object sender, RoutedEventArgs e) =>
        AssistantSaveKeyButton.IsEnabled = AssistantKeyBox.Password.Trim().Length > 0;

    private void OnAssistantSaveKey(object sender, RoutedEventArgs e)
    {
        if (CurrentAssistantProvider is not { } p) return;
        try
        {
            _services.AssistantKeys.Set(p.Id, AssistantKeyBox.Password);
        }
        catch (ArgumentException ex)
        {
            ShowAssistantResult(InfoBarSeverity.Error, "Chave não aceita", ex.Message);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            AppLog.Error("Saving the assistant key failed", ex);
            ShowAssistantResult(InfoBarSeverity.Error, "Não consegui salvar a chave", "O Windows não permitiu gravar o arquivo protegido. Veja o registro de erros.");
            return;
        }
        RefreshAssistantProvider();
        ShowAssistantResult(InfoBarSeverity.Success, "Chave salva", "Use “Testar conexão” para confirmar que ela funciona.");
    }

    private void OnAssistantRemoveKey(object sender, RoutedEventArgs e)
    {
        if (CurrentAssistantProvider is not { } p) return;
        _services.AssistantKeys.Remove(p.Id);
        RefreshAssistantProvider();
        ShowAssistantResult(InfoBarSeverity.Informational, "Chave removida", "Ela foi apagada deste computador.");
    }

    // ------------------------------------------------------------------------------------------ network

    private async void OnAssistantListModels(object sender, RoutedEventArgs e)
    {
        if (CurrentAssistantProvider is not { } p) return;
        await RunAssistantOperation(async ct =>
        {
            var client = AssistantSetup.CreateClientForModelList(_services.Settings.Current, _services.AssistantProviders, _services.AssistantKeys);
            var all = await client.ListModelsAsync(ct);
            var choices = AssistantSetup.ModelChoices(p.Id, all);
            _modelChoices[p.Id] = choices;
            if (CurrentAssistantProvider?.Id == p.Id) SetAssistantModelChoices(choices, AssistantSetup.ModelFor(_services.Settings.Current, p));
            return choices.Count == 0
                ? $"O provedor devolveu {all.Count} modelos, nenhum de conversa. Digite o nome do modelo."
                : $"{choices.Count} modelos de conversa encontrados (de {all.Count} no total).";
        });
    }

    private async void OnAssistantTest(object sender, RoutedEventArgs e)
    {
        await RunAssistantOperation(async ct =>
        {
            var settings = _services.Settings.Current;
            var model = AssistantSetup.Resolve(settings, _services.AssistantProviders, _services.AssistantKeys)?.Model ?? "";
            var client = AssistantSetup.CreateClient(settings, _services.AssistantProviders, _services.AssistantKeys);
            var clock = Stopwatch.StartNew();
            await client.TestAsync(ct);
            return $"Conectado: o modelo “{model}” respondeu em {clock.Elapsed.TotalSeconds:0.0} s.";
        });
    }

    /// <summary>Runs one network operation with the buttons locked and a spinner, and reports the outcome in the card. The error text never contains the key.</summary>
    private async Task RunAssistantOperation(Func<CancellationToken, Task<string>> work)
    {
        if (_assistantBusy) return;
        _assistantBusy = true;
        AssistantTestButton.IsEnabled = false;
        AssistantListButton.IsEnabled = false;
        AssistantBusy.IsActive = true;
        AssistantResult.IsOpen = false;
        _assistantCts = new CancellationTokenSource();
        try
        {
            string message = await work(_assistantCts.Token);
            ShowAssistantResult(InfoBarSeverity.Success, "Tudo certo", message);
        }
        catch (AssistantException ex)
        {
            ShowAssistantResult(InfoBarSeverity.Error, "Não deu certo", ex.Message);
        }
        catch (OperationCanceledException)
        {
            // the window was closed
        }
        catch (Exception ex)
        {
            AppLog.Error("Assistant connection check failed", ex);
            ShowAssistantResult(InfoBarSeverity.Error, "Erro inesperado", "Veja o registro de erros para detalhes.");
        }
        finally
        {
            AssistantBusy.IsActive = false;
            AssistantTestButton.IsEnabled = true;
            AssistantListButton.IsEnabled = true;
            _assistantBusy = false;
        }
    }

    private void OnAssistantOpenPromptFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = Path.GetDirectoryName(AppPaths.AssistantPromptFile)!;
            Directory.CreateDirectory(folder);
            ShellLauncher.Open(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowAssistantResult(InfoBarSeverity.Error, "Não consegui abrir a pasta", ex.Message);
        }
    }
}
