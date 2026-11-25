using Apparatus.Core.Capture;
using Apparatus.Core.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Apparatus.App;

/// <summary>The "Captura de tela" page: switch, hotkeys, saving options, and what the switch does to the Windows Snipping Tool.</summary>
public sealed partial class SettingsView
{
    private bool _capturingShot;
    private CaptureKind _shotKind;
    private bool _captureKeyUpHooked;

    private static readonly HotkeySpec DefaultCaptureHotkey = new(HotkeyModifiers.None, HotkeySpec.PrintScreenKey);
    private static readonly HotkeySpec DefaultQuickHotkey = new(HotkeyModifiers.Shift, HotkeySpec.PrintScreenKey);

    private void LoadCapture()
    {
        var s = _services.Settings.Current;
        bool before = _loading;
        _loading = true;
        CaptureSwitch.IsOn = s.CaptureEnabled;
        CaptureNotifySwitch.IsOn = s.CaptureNotifyOnSave;
        CaptureScopeBox.SelectedIndex = s.CaptureAutoAllMonitors ? 1 : 0;
        CaptureFormatBox.SelectedIndex = CaptureImageFormats.Parse(s.CaptureFormat) == CaptureImageFormat.Jpeg ? 1 : 0;
        CaptureQualityBox.Value = CaptureImageFormats.ClampQuality(s.CaptureQuality);
        _loading = before;

        CaptureHotkeyText.Text = s.CaptureHotkey;
        QuickHotkeyText.Text = s.CaptureQuickHotkey;
        CaptureNowButton.IsEnabled = s.CaptureEnabled;
        RefreshCaptureFolder();
        RefreshCaptureWindowsStatus();

        if (!_captureKeyUpHooked)
        {
            _captureKeyUpHooked = true;
            // Windows delivers Print Screen as a key release only, so recording it has to listen to that too.
            Root.PreviewKeyUp += (_, e) => { if (_capturingShot && e.Key == VirtualKey.Snapshot) OnCaptureHotkeyKey(e); };
        }
    }

    private void RefreshCaptureWindowsStatus()
    {
        var capture = _services.CaptureHost;
        if (capture is null) return;
        string state = capture.WindowsSnippingOnPrintScreen ? "ligada" : "desligada";
        CaptureWindowsStatus.Text = capture.IsEnabled
            ? $"Opção do Windows “Print Screen abre a captura de tela”: {state}. O Apparatus a desliga enquanto estiver ativado e a restaura ao desativar."
            : $"Opção do Windows “Print Screen abre a captura de tela”: {state}. Fica como está até você ativar a captura do Apparatus.";
    }

    private void RefreshCaptureFolder()
    {
        bool custom = !string.IsNullOrWhiteSpace(_services.Settings.Current.CaptureSaveFolder);
        string folder = _services.CaptureHost?.SaveFolder
            ?? CaptureNaming.SaveFolder(_services.Settings.Current.CaptureSaveFolder, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        CaptureFolderText.Text = custom ? folder : $"{folder} (padrão)";
        CaptureFolderResetButton.IsEnabled = custom;
    }

    private void OnCaptureToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _services.CaptureHost is not { } capture) return;

        if (CaptureSwitch.IsOn)
        {
            var outcome = capture.Enable();
            if (!outcome.Success)
            {
                _loading = true;
                CaptureSwitch.IsOn = false;
                _loading = false;
                ShowCaptureMessage(outcome.Message, InfoBarSeverity.Error);
            }
            else if (outcome.Message.Length > 0) ShowCaptureMessage(outcome.Message, InfoBarSeverity.Warning);
            else ShowCaptureMessage($"Ativado. Pressione {capture.ActiveHotkey} para capturar. A Ferramenta de Captura do Windows deixou de abrir nessa tecla.", InfoBarSeverity.Success);
        }
        else
        {
            capture.Disable();
            ShowCaptureMessage("Desativado. A opção do Windows voltou ao valor de antes.", InfoBarSeverity.Success);
        }

        CaptureNowButton.IsEnabled = capture.IsEnabled;
        RefreshCaptureWindowsStatus();
    }

    private void OnCaptureNow(object sender, RoutedEventArgs e)
    {
        // The settings live inside the launcher window: it must be out of the way before the screen is read.
        ((App)Application.Current).CaptureFromSettings();
    }

    // ----------------------------------------------------------- saving

    private void OnCaptureNotifyToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.ApplySettings(s => s.CaptureNotifyOnSave = CaptureNotifySwitch.IsOn);
    }

    private void OnCaptureFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CaptureFormatBox.SelectedIndex < 0) return;
        var format = CaptureFormatBox.SelectedIndex == 1 ? CaptureImageFormat.Jpeg : CaptureImageFormat.Png;
        _services.ApplySettings(s => s.CaptureFormat = CaptureImageFormats.Id(format));
    }

    private void OnCaptureQualityChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        if (double.IsNaN(args.NewValue))
        {
            // The box was emptied: go back to what was saved.
            sender.Value = CaptureImageFormats.ClampQuality(_services.Settings.Current.CaptureQuality);
            return;
        }
        int quality = CaptureImageFormats.ClampQuality((int)Math.Round(args.NewValue));
        _services.ApplySettings(s => s.CaptureQuality = quality);
    }

    private void OnCaptureScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CaptureScopeBox.SelectedIndex < 0) return;
        _services.ApplySettings(s => s.CaptureAutoAllMonitors = CaptureScopeBox.SelectedIndex == 1);
    }

    private async void OnChooseCaptureFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        _services.ApplySettings(s => s.CaptureSaveFolder = folder.Path);
        RefreshCaptureFolder();
        ShowSaveMessage($"A captura automática salva em {folder.Path}.", InfoBarSeverity.Success);
    }

    private void OnResetCaptureFolder(object sender, RoutedEventArgs e)
    {
        _services.ApplySettings(s => s.CaptureSaveFolder = "");
        RefreshCaptureFolder();
        ShowSaveMessage("Voltou para a pasta padrão (Imagens\\Screenshots).", InfoBarSeverity.Success);
    }

    // ------------------------------------------------------------ hotkeys

    private CaptureKind KindOf(object sender) =>
        ReferenceEquals(sender, QuickChangeButton) || ReferenceEquals(sender, QuickResetButton) ? CaptureKind.Automatic : CaptureKind.Normal;

    private void OnChangeCaptureHotkey(object sender, RoutedEventArgs e)
    {
        var kind = KindOf(sender);
        if (_capturingShot) { bool same = kind == _shotKind; StopCapturingShot(); if (same) return; }
        if (_capturing) StopCapturing(resume: true);
        _capturingShot = true;
        _shotKind = kind;
        _services.SuspendHotkey();
        _services.ActionHost?.SuspendHotkeys();
        _services.CaptureHost?.Suspend();

        var (button, text, help) = ControlsOf(kind);
        button.Content = "Cancelar";
        text.Text = "Pressione…";
        help.Text = "Pressione a nova combinação (Print Screen sozinha também vale). Esc cancela.";
        ShowCaptureMessage("", InfoBarSeverity.Error);
        ShowSaveMessage("", InfoBarSeverity.Error);
        button.Focus(FocusState.Programmatic);
    }

    private void OnResetCaptureHotkey(object sender, RoutedEventArgs e)
    {
        var kind = KindOf(sender);
        if (_capturingShot) StopCapturingShot();
        ApplyCaptureHotkey(kind == CaptureKind.Normal ? DefaultCaptureHotkey : DefaultQuickHotkey, kind);
    }

    private (Button Button, TextBlock Text, TextBlock Help) ControlsOf(CaptureKind kind) => kind == CaptureKind.Normal
        ? (CaptureChangeButton, CaptureHotkeyText, CaptureHotkeyHelp)
        : (QuickChangeButton, QuickHotkeyText, QuickHotkeyHelp);

    private void StopCapturingShot()
    {
        _menuGuardUntil = Environment.TickCount64 + 800;
        _capturingShot = false;
        var s = _services.Settings.Current;
        CaptureChangeButton.Content = QuickChangeButton.Content = "Alterar";
        CaptureHotkeyHelp.Text = QuickHotkeyHelp.Text = "Clique em Alterar e pressione a nova combinação.";
        CaptureHotkeyText.Text = s.CaptureHotkey;
        QuickHotkeyText.Text = s.CaptureQuickHotkey;
        _services.ResumeHotkey();
        _services.ActionHost?.ResumeHotkeys();
        _services.CaptureHost?.Resume();
    }

    private void OnCaptureHotkeyKey(KeyRoutedEventArgs e)
    {
        e.Handled = true;
        uint vk = (uint)e.Key;
        if (HotkeySpec.IsModifierKey(vk)) return;

        var mods = HotkeyModifiers.None;
        if (IsDown(VirtualKey.Control)) mods |= HotkeyModifiers.Control;
        if (IsDown(VirtualKey.Menu)) mods |= HotkeyModifiers.Alt;
        if (IsDown(VirtualKey.Shift)) mods |= HotkeyModifiers.Shift;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) mods |= HotkeyModifiers.Win;

        if (e.Key == VirtualKey.Escape && mods == HotkeyModifiers.None)
        {
            StopCapturingShot();
            return;
        }

        var spec = new HotkeySpec(mods, vk);
        var kind = _shotKind;
        if (!spec.IsValid)
        {
            Message(kind, "Use pelo menos um modificador (Ctrl, Alt, Shift ou Win) junto com a tecla. Print Screen e as teclas F podem ser usadas sozinhas.", InfoBarSeverity.Warning);
            return;
        }

        StopCapturingShot();
        ApplyCaptureHotkey(spec, kind);
    }

    private void ApplyCaptureHotkey(HotkeySpec spec, CaptureKind kind)
    {
        if (_services.CaptureHost is not { } capture) return;
        var outcome = capture.ChangeHotkey(spec, kind);
        var s = _services.Settings.Current;
        CaptureHotkeyText.Text = s.CaptureHotkey;
        QuickHotkeyText.Text = s.CaptureQuickHotkey;
        Message(kind, outcome.Message, outcome.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private void Message(CaptureKind kind, string text, InfoBarSeverity severity)
    {
        if (kind == CaptureKind.Normal) ShowCaptureMessage(text, severity);
        else ShowSaveMessage(text, severity);
    }

    private void ShowCaptureMessage(string text, InfoBarSeverity severity)
    {
        CaptureInfo.Severity = severity;
        CaptureInfo.Message = text;
        CaptureInfo.IsOpen = text.Length > 0;
    }

    private void ShowSaveMessage(string text, InfoBarSeverity severity)
    {
        CaptureSaveInfo.Severity = severity;
        CaptureSaveInfo.Message = text;
        CaptureSaveInfo.IsOpen = text.Length > 0;
    }
}
