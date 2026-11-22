using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Apparatus.App;

/// <summary>The updates card of the settings window.</summary>
public sealed partial class SettingsWindow
{
    private bool _updatesHooked;

    private void LoadUpdates()
    {
        UpdateSourceBox.Text = _services.Settings.Current.UpdateSource;
        RefreshUpdates();
        if (_updatesHooked) return;
        _updatesHooked = true;
        void OnStatus() => DispatcherQueue.TryEnqueue(RefreshUpdates);
        _services.Updates.StatusChanged += OnStatus;
        Closed += (_, _) => _services.Updates.StatusChanged -= OnStatus;
    }

    private void RefreshUpdates()
    {
        var u = _services.Updates;
        string running = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        VersionText.Text = u.IsInstalled ? $"Apparatus {u.InstalledVersion} (instalado)" : $"Apparatus {running} (executando de uma pasta de build, sem instalador)";
        UpdateStatusText.Text = u.Status;
        InstallUpdateButton.Visibility = u.AvailableVersion is null ? Visibility.Collapsed : Visibility.Visible;
        if (u.AvailableVersion is { } v) InstallUpdateButton.Content = $"Instalar a versão {v} e reiniciar";
    }

    private void OnUpdateSourceLostFocus(object sender, RoutedEventArgs e)
    {
        string value = UpdateSourceBox.Text.Trim();
        if (value == _services.Settings.Current.UpdateSource) return;
        _services.ApplySettings(s => s.UpdateSource = value);
        RefreshUpdates();
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        OnUpdateSourceLostFocus(sender, e);
        UpdateStatusText.Text = "Verificando…";
        await _services.Updates.CheckAsync();
        RefreshUpdates();
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        InstallUpdateButton.IsEnabled = false;
        await _services.Updates.InstallAndRestartAsync();   // on success the program restarts and never returns here
        InstallUpdateButton.IsEnabled = true;
        RefreshUpdates();
    }
}
