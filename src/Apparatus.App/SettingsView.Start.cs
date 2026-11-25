using Apparatus.Core;
using Apparatus.Core.Start;
using Apparatus.Native;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Apparatus.App;

/// <summary>The Start menu and Windows-tweak cards of the settings window.</summary>
public sealed partial class SettingsView
{
    private readonly Dictionary<string, (ComboBox Box, Button Restore, TextBlock Status)> _tweakRows = new();

    private void LoadStart()
    {
        var s = _services.Settings.Current;
        StartKeySwitch.IsOn = s.StartMenuEnabled;
        Select(StartThemeBox, s.StartTheme);
        Select(StartBackdropBox, s.StartBackdrop);
        Select(StartPositionBox, s.StartPosition);
        StartColumnsBox.Value = Math.Clamp(s.StartColumns, 4, 8);
        StartRecentSwitch.IsOn = s.StartShowRecent;
        StartClickSwitch.IsOn = s.StartButtonClick;
        BuildTweakRows();
        HookStartStatus();
        RefreshStartStatus();
    }

    private bool _startStatusHooked;

    private void HookStartStatus()
    {
        if (_startStatusHooked || _services.StartHost is not { } host) return;
        _startStatusHooked = true;
        void OnTripped(string _) => DispatcherQueue.TryEnqueue(RefreshStartStatus);
        host.FailsafeTripped += OnTripped;
        Closed += (_, _) => host.FailsafeTripped -= OnTripped;

        // The location of the Start button is found in the background; look again shortly after opening.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.Tick += (_, _) => RefreshStartStatus();
        timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    /// <summary>What the Windows-key/click replacement is doing, and the failsafe's say in it.</summary>
    private void RefreshStartStatus()
    {
        if (_services.StartHost is not { } host) return;
        var f = host.Failsafe;
        bool enabled = _services.Settings.Current.StartMenuEnabled;

        if (f.IsTripped)
        {
            var retry = new Button { Content = "Tentar de novo" };
            retry.Click += (_, _) =>
            {
                _services.ApplySettings(s => s.StartMenuEnabled = true);
                StartKeySwitch.IsOn = true;
                RefreshStartStatus();
            };
            StartStatus.ActionButton = retry;
            StartStatus.Severity = InfoBarSeverity.Warning;
            StartStatus.Title = "Desligado automaticamente — o menu do Windows está em uso";
            StartStatus.Message = $"Motivo: {f.TrippedReason}";
        }
        else if (enabled)
        {
            StartStatus.ActionButton = null;
            StartStatus.Severity = f.InProbation ? InfoBarSeverity.Informational : InfoBarSeverity.Success;
            StartStatus.Title = f.InProbation
                ? $"Ativo — observando o Windows {f.CurrentBuild}"
                : $"Ativo — funcionando no Windows {f.CurrentBuild}";
            StartStatus.Message = f.InProbation
                ? "Nas primeiras vezes depois de uma atualização do Windows, qualquer falha devolve o menu ao Windows automaticamente."
                : "Se uma atualização do Windows quebrar a substituição, o menu do Windows volta sozinho.";
        }
        else
        {
            StartStatus.IsOpen = false;
            StartDiag.Text = "";
            return;
        }
        StartStatus.IsOpen = true;

        int buttons = host.ButtonRects.Count;
        StartDiag.Text = !enabled || !_services.Settings.Current.StartButtonClick ? ""
            : buttons > 0 ? $"Botão Iniciar da barra de tarefas localizado ({buttons})."
            : "Procurando o botão Iniciar da barra de tarefas… (se não for encontrado, só a tecla Windows abre o menu do Apparatus)";
    }

    private void OnStartClickToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.ApplySettings(s => s.StartButtonClick = StartClickSwitch.IsOn);
        RefreshStartStatus();
    }

    private static void Select(ComboBox box, string tag)
    {
        var items = box.Items.OfType<ComboBoxItem>().ToList();
        box.SelectedItem = items.FirstOrDefault(i => (string)i.Tag == tag) ?? items.FirstOrDefault();
    }

    private static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    // --------------------------------------------------------- start menu

    private void OnStartKeyToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.ApplySettings(s => s.StartMenuEnabled = StartKeySwitch.IsOn);
        RefreshStartStatus();
    }

    private void OnStartAppearanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        string theme = TagOf(StartThemeBox), backdrop = TagOf(StartBackdropBox), position = TagOf(StartPositionBox);
        _services.ApplySettings(s =>
        {
            if (theme.Length > 0) s.StartTheme = theme;
            if (backdrop.Length > 0) s.StartBackdrop = backdrop;
            if (position.Length > 0) s.StartPosition = position;
        });
    }

    private void OnStartColumnsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        _services.ApplySettings(s => s.StartColumns = (int)Math.Clamp(args.NewValue, 4, 8));
    }

    private void OnStartAppearanceToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.ApplySettings(s => s.StartShowRecent = StartRecentSwitch.IsOn);
    }

    private void OnOpenStartMenu(object sender, RoutedEventArgs e) => _services.StartHost?.Show();

    private async void OnResetStartLayout(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Esvaziar os fixados?",
                Content = "Todos os itens fixados saem do menu Iniciar do Apparatus. As seções e os atalhos da barra de baixo permanecem.",
                PrimaryButtonText = "Esvaziar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            foreach (var item in _services.StartLayout.Snapshot().Sections.SelectMany(sec => sec.Items).ToList())
                _services.StartLayout.Unpin(item.Key);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Clearing the Start layout failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ tweaks

    private void BuildTweakRows()
    {
        TweaksList.Children.Clear();
        _tweakRows.Clear();
        foreach (var tweak in TweakCatalog.All)
        {
            var box = new ComboBox { MinWidth = 210, VerticalAlignment = VerticalAlignment.Center, Tag = tweak.Id };
            foreach (var option in tweak.Options) box.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
            var restore = new Button { Content = "Desfazer", VerticalAlignment = VerticalAlignment.Center, Tag = tweak.Id };
            var status = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            texts.Children.Add(new TextBlock { Text = tweak.Title, TextWrapping = TextWrapping.Wrap });
            texts.Children.Add(new TextBlock { Text = tweak.Description, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
            texts.Children.Add(status);

            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(texts);
            Grid.SetColumn(box, 1);
            Grid.SetColumn(restore, 2);
            row.Children.Add(box);
            row.Children.Add(restore);
            TweaksList.Children.Add(row);

            _tweakRows[tweak.Id] = (box, restore, status);
            RefreshTweakRow(tweak);
            box.SelectionChanged += OnTweakChanged;
            restore.Click += OnRestoreTweak;
        }
    }

    private void RefreshTweakRow(WindowsTweak tweak)
    {
        var (box, restore, status) = _tweakRows[tweak.Id];
        int current = _services.Tweaks.Current(tweak.Id);
        _tweakLoading = true;
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == current);
        _tweakLoading = false;
        bool changed = _services.Tweaks.HasBackup(tweak.Id);
        restore.IsEnabled = changed;
        status.Text = changed ? "Alterado pelo Apparatus" : "";
        status.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _tweakLoading;

    private void OnTweakChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_tweakLoading || sender is not ComboBox { Tag: string id, SelectedItem: ComboBoxItem { Tag: int value } }) return;
        var result = _services.Tweaks.Apply(id, value);
        ShowTweakResult(result, TweakCatalog.Get(id).Title);
        RefreshTweakRow(TweakCatalog.Get(id));
    }

    private void OnRestoreTweak(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var result = _services.Tweaks.Restore(id);
        ShowTweakResult(result, TweakCatalog.Get(id).Title);
        RefreshTweakRow(TweakCatalog.Get(id));
    }

    private void OnRestoreAllTweaks(object sender, RoutedEventArgs e)
    {
        int restored = _services.Tweaks.RestoreAll();
        foreach (var tweak in TweakCatalog.All) RefreshTweakRow(tweak);
        TweaksInfo.Severity = InfoBarSeverity.Success;
        TweaksInfo.Title = restored == 0 ? "Nada a desfazer." : $"{restored} ajuste(s) desfeito(s).";
        TweaksInfo.Message = restored == 0 ? "" : "Reinicie o Explorer para ver o efeito de todos.";
        TweaksInfo.IsOpen = true;
    }

    private void ShowTweakResult(TweakResult result, string title)
    {
        TweaksInfo.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        TweaksInfo.Title = title;
        TweaksInfo.Message = result.Message;
        TweaksInfo.IsOpen = true;
    }

    private async void OnRestartExplorer(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Reiniciar o Explorer?",
                Content = "A barra de tarefas e a área de trabalho somem por alguns segundos, e as janelas abertas do Explorador de Arquivos são fechadas. Nenhum outro programa é afetado.",
                PrimaryButtonText = "Reiniciar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ExplorerControl.RestartAsync();
            TweaksInfo.Severity = InfoBarSeverity.Success;
            TweaksInfo.Title = "Explorer reiniciado";
            TweaksInfo.Message = "";
            TweaksInfo.IsOpen = true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Restarting Explorer failed", ex);
            TweaksInfo.Severity = InfoBarSeverity.Error;
            TweaksInfo.Title = "Não foi possível reiniciar o Explorer";
            TweaksInfo.Message = ex.Message;
            TweaksInfo.IsOpen = true;
        }
    }
}
