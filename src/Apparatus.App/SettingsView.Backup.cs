using Apparatus.Core;
using Apparatus.Core.Backup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Apparatus.App;

/// <summary>The backup and sync-folder card of the settings window.</summary>
public sealed partial class SettingsView
{
    private bool _syncHooked;

    private void LoadBackup()
    {
        RefreshSyncCard();
        if (_syncHooked) return;
        _syncHooked = true;
        _services.Sync.StatusChanged += OnSyncStatusChanged;
        Closed += (_, _) => _services.Sync.StatusChanged -= OnSyncStatusChanged;
    }

    private void OnSyncStatusChanged() => DispatcherQueue.TryEnqueue(RefreshSyncCard);

    private void RefreshSyncCard()
    {
        string? folder = _services.Sync.Folder;
        SyncFolderText.Text = folder ?? "Desligada.";
        SyncOffButton.IsEnabled = folder is not null;
        SyncNowButton.IsEnabled = folder is not null;
        SyncImportButton.IsEnabled = folder is not null && _services.Sync.PeekFolderFile() is not null;
        SyncStatusText.Text = _services.Sync.Status;
    }

    private void ShowBackup(string title, string message, InfoBarSeverity severity)
    {
        BackupInfo.Severity = severity;
        BackupInfo.Title = title;
        BackupInfo.Message = message;
        BackupInfo.IsOpen = true;
    }

    // ------------------------------------------------------------- export

    private async void OnExportBackup(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = $"apparatus-{DateTime.Now:yyyy-MM-dd}" };
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeChoices.Add("Backup do Apparatus", new List<string> { BackupBundle.Extension });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            using (var stream = File.Create(file.Path)) _services.Backup.Export(stream);
            ShowBackup("Backup gravado", file.Path, InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowBackup("Não foi possível gravar o backup", ex.Message, InfoBarSeverity.Error);
        }
    }

    // ------------------------------------------------------------- import

    private async void OnImportBackup(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeFilter.Add(BackupBundle.Extension);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            BackupContent content;
            using (var stream = File.OpenRead(file.Path)) content = BackupBundle.Read(stream);
            await ConfirmAndImportAsync(content, afterImport: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BackupFormatException)
        {
            ShowBackup("Não foi possível ler o backup", ex.Message, InfoBarSeverity.Error);
        }
    }

    /// <summary>Shows what the file holds, lets the user pick the parts, and applies them.</summary>
    private async Task ConfirmAndImportAsync(BackupContent content, Func<BackupParts, BackupImportResult?>? afterImport)
    {
        var m = content.Manifest;
        var settings = new CheckBox { Content = "Configurações (aparência, buscas na web, menu Iniciar…)", IsChecked = content.SettingsJson is not null, IsEnabled = content.SettingsJson is not null };
        var snippets = new CheckBox { Content = $"Snippets ({m.Snippets})", IsChecked = content.SnippetsJson is not null, IsEnabled = content.SnippetsJson is not null };
        var actions = new CheckBox { Content = $"Ações e macros ({m.Actions})", IsChecked = content.ActionsJson is not null, IsEnabled = content.ActionsJson is not null };
        var start = new CheckBox { Content = $"Menu Iniciar — substitui os fixados atuais ({m.PinnedItems})", IsChecked = content.StartJson is not null, IsEnabled = content.StartJson is not null };

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = $"Feito em {m.Machine}, {m.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}.", Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(settings);
        body.Children.Add(snippets);
        body.Children.Add(actions);
        body.Children.Add(start);
        body.Children.Add(new TextBlock
        {
            Text = "Snippets e ações com o mesmo nome são atualizados; os demais são mantidos. Ações podem executar programas e scripts: importe apenas backups seus ou de fonte confiável.",
            FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            Title = "Importar backup",
            Content = body,
            PrimaryButtonText = "Importar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var parts = BackupParts.None;
        if (settings.IsChecked == true) parts |= BackupParts.Settings;
        if (snippets.IsChecked == true) parts |= BackupParts.Snippets;
        if (actions.IsChecked == true) parts |= BackupParts.Actions;
        if (start.IsChecked == true) parts |= BackupParts.StartLayout;
        if (parts == BackupParts.None) return;

        var result = afterImport is not null ? afterImport(parts) : _services.Backup.Import(content, parts);
        if (result is null)
        {
            ShowBackup("Nada importado", "O arquivo não está mais disponível.", InfoBarSeverity.Warning);
            return;
        }
        ReportImport(result);
        LoadFromSettings();
    }

    private void ReportImport(BackupImportResult r)
    {
        var parts = new List<string>();
        if (r.SettingsApplied) parts.Add("configurações aplicadas");
        if (r.Snippets is { } s) parts.Add($"snippets: {s.Imported} novo(s), {s.Updated} atualizado(s)");
        if (r.Actions is { } a) parts.Add($"ações: {a.Imported} nova(s), {a.Updated} atualizada(s)");
        if (r.PinnedItems > 0) parts.Add($"{r.PinnedItems} item(ns) fixado(s) no Iniciar");

        var skipped = (r.Snippets?.Skipped.Count ?? 0) + (r.Actions?.Skipped.Count ?? 0);
        string message = parts.Count == 0 ? "Nada foi alterado." : string.Join("; ", parts) + ".";
        if (skipped > 0) message += $" {skipped} item(ns) ignorado(s) por conflito ou erro.";
        foreach (var p in r.Problems) message += " " + p;

        ShowBackup(r.Problems.Count > 0 ? "Importação parcial" : "Backup importado", message,
            r.Problems.Count > 0 || skipped > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    // ---------------------------------------------------------- sync folder

    private async void OnChooseSyncFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            _services.ApplySettings(s => s.SyncFolder = folder.Path);
            bool written = _services.Sync.HasUnseenChanges() ? false : _services.Sync.WriteNow();
            RefreshSyncCard();
            if (!written && _services.Sync.HasUnseenChanges())
                ShowBackup("A pasta já tem um backup de outro computador", "Use “Importar da pasta…” para trazê-lo; depois o Apparatus passa a gravar por cima.", InfoBarSeverity.Informational);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowBackup("Não foi possível usar a pasta", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnSyncOff(object sender, RoutedEventArgs e)
    {
        _services.ApplySettings(s => s.SyncFolder = "");
        RefreshSyncCard();
    }

    private void OnSyncNow(object sender, RoutedEventArgs e)
    {
        _services.Sync.WriteNow();
        RefreshSyncCard();
    }

    private async void OnSyncImport(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_services.Sync.FilePath is not { } path || !File.Exists(path)) { RefreshSyncCard(); return; }
            BackupContent content;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) content = BackupBundle.Read(stream);
            await ConfirmAndImportAsync(content, parts => _services.Sync.ImportFromFolder(parts));
            RefreshSyncCard();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BackupFormatException)
        {
            ShowBackup("Não foi possível ler o arquivo da pasta", ex.Message, InfoBarSeverity.Error);
        }
    }
}
