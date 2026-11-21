using System.Collections.ObjectModel;
using Apparatus.Core.Indexing;
using Apparatus.Core.Input;
using Apparatus.Native;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Apparatus.App;

public sealed partial class SettingsWindow : Window
{
    private readonly AppServices _services;
    private readonly nint _hwnd;
    private readonly ObservableCollection<string> _roots = new();
    private bool _loading = true;
    private bool _capturing;

    public SettingsWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        AppWindow.SetIcon(AppPaths.AssetPath("apparatus.ico"));
        double scale = WindowHelpers.GetScale(_hwnd);
        AppWindow.Resize(new SizeInt32((int)(860 * scale), (int)(920 * scale)));

        RootsList.ItemsSource = _roots;
        DataPathText.Text = $"{AppPaths.DataDirectory}\nConfiguração: {AppPaths.SettingsFile}";
        LoadFromSettings();

        _services.Index.StatusChanged += OnIndexStatusChanged;
        Closed += (_, _) =>
        {
            _services.Index.StatusChanged -= OnIndexStatusChanged;
            if (_capturing)
            {
                _services.ResumeHotkey();
                _services.ActionHost?.ResumeHotkeys();
            }
        };
        _loading = false;
    }

    private void LoadFromSettings()
    {
        var s = _services.Settings.Current;
        HotkeyText.Text = s.Hotkey;
        AutostartSwitch.IsOn = s.RunAtStartup;
        MaxResultsBox.Value = s.MaxResults;
        EngineBox.ItemsSource = s.SearchEngines;
        EngineBox.SelectedItem = s.SearchEngines.FirstOrDefault(e => e.Keyword == s.DefaultSearchEngine) ?? s.SearchEngines.FirstOrDefault();
        ExpansionSwitch.IsOn = s.SnippetExpansion;
        ExcludedProcessesBox.Text = string.Join("\r", s.SnippetExcludedProcesses);
        IndexSwitch.IsOn = s.IndexFiles;
        RootsMode.SelectedIndex = s.IndexRoots is null ? 0 : 1;
        _roots.Clear();
        foreach (var r in s.IndexRoots ?? new()) _roots.Add(r);
        ExcludedBox.Text = string.Join("\r", s.ExcludedNames);
        UpdateRootsVisibility();
        UpdateIndexStatus();
    }

    // ------------------------------------------------------------ hotkey

    private void OnChangeHotkey(object sender, RoutedEventArgs e)
    {
        if (_capturing) { StopCapturing(resume: true); return; }
        _capturing = true;
        _services.SuspendHotkey();   // so pressing the current combination does not open the launcher
        _services.ActionHost?.SuspendHotkeys();   // nor run an action that uses the same keys
        ChangeHotkeyButton.Content = "Cancelar";
        HotkeyText.Text = "Pressione…";
        HotkeyInfo.IsOpen = false;
        HotkeyHelp.Text = "Pressione a nova combinação (por exemplo Alt+Espaço). Esc cancela.";
        ChangeHotkeyButton.Focus(FocusState.Programmatic);
    }

    private void OnResetHotkey(object sender, RoutedEventArgs e)
    {
        if (_capturing) StopCapturing(resume: true);
        TryApplyHotkey(HotkeySpec.Default);
    }

    private void StopCapturing(bool resume)
    {
        _capturing = false;
        ChangeHotkeyButton.Content = "Alterar";
        HotkeyHelp.Text = "Clique em Alterar e pressione a nova combinação.";
        HotkeyText.Text = _services.Settings.Current.Hotkey;
        if (resume) _services.ResumeHotkey();
        _services.ActionHost?.ResumeHotkeys();
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;

        uint vk = (uint)e.Key;
        if (HotkeySpec.IsModifierKey(vk)) return;   // wait for the "real" key

        var mods = HotkeyModifiers.None;
        if (IsDown(VirtualKey.Control)) mods |= HotkeyModifiers.Control;
        if (IsDown(VirtualKey.Menu)) mods |= HotkeyModifiers.Alt;
        if (IsDown(VirtualKey.Shift)) mods |= HotkeyModifiers.Shift;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) mods |= HotkeyModifiers.Win;

        if (e.Key == VirtualKey.Escape && mods == HotkeyModifiers.None)
        {
            StopCapturing(resume: true);
            return;
        }

        var spec = new HotkeySpec(mods, vk);
        if (!spec.IsValid)
        {
            ShowHotkeyMessage("Use pelo menos um modificador (Ctrl, Alt, Shift ou Win) junto com a tecla.", InfoBarSeverity.Warning);
            return;
        }

        StopCapturing(resume: false);
        TryApplyHotkey(spec);
    }

    private void TryApplyHotkey(HotkeySpec spec)
    {
        // Windows would report these keys as "taken by another app" because the action registered them in this very process.
        if (_services.FindActionUsing(spec) is { } actionName)
        {
            _services.ResumeHotkey();
            HotkeyText.Text = _services.Settings.Current.Hotkey;
            ShowHotkeyMessage($"{spec} já é o atalho da ação “{actionName}”. Escolha outra combinação ou mude o atalho dessa ação.", InfoBarSeverity.Error);
            return;
        }

        var result = _services.TryChangeHotkey(spec);
        HotkeyText.Text = _services.Settings.Current.Hotkey;
        if (result.Success)
        {
            ShowHotkeyMessage($"Atalho alterado para {spec}.", InfoBarSeverity.Success);
            return;
        }

        string reason = result.Win32Error == 1409
            ? $"{spec} já está em uso por outro aplicativo (o Raycast, por exemplo). Feche-o ou escolha outra combinação."
            : $"Não foi possível registrar {spec} (erro {result.Win32Error}).";
        if (_services.ActiveHotkey is { } active) reason += $" Continua valendo {active}.";
        else reason += " Nenhum atalho está ativo no momento.";
        ShowHotkeyMessage(reason, InfoBarSeverity.Error);
    }

    private void ShowHotkeyMessage(string text, InfoBarSeverity severity)
    {
        HotkeyInfo.Severity = severity;
        HotkeyInfo.Message = text;
        HotkeyInfo.IsOpen = true;
    }

    // ----------------------------------------------------------- general

    private void OnAutostartToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.ApplySettings(s => s.RunAtStartup = AutostartSwitch.IsOn);
    }

    private void OnMaxResultsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        _services.ApplySettings(s => s.MaxResults = (int)Math.Clamp(args.NewValue, 3, 12));
    }

    private void OnEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EngineBox.SelectedItem is not Core.Settings.WebSearchEngine engine) return;
        _services.ApplySettings(s => s.DefaultSearchEngine = engine.Keyword);
    }

    // ---------------------------------------------------------- snippets

    private void OnExpansionToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.ApplySettings(s => s.SnippetExpansion = ExpansionSwitch.IsOn);
    }

    private void OnExcludedProcessesLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var names = ExcludedProcessesBox.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.SequenceEqual(_services.Settings.Current.SnippetExcludedProcesses, StringComparer.OrdinalIgnoreCase)) return;
        _services.ApplySettings(s => s.SnippetExcludedProcesses = names);
    }

    private void OnOpenSnippets(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenSnippets();

    // ------------------------------------------------------------- files

    private void OnIndexToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.ApplySettings(s => s.IndexFiles = IndexSwitch.IsOn);
    }

    private void OnRootsModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        bool custom = RootsMode.SelectedIndex == 1;
        if (custom && _roots.Count == 0)
        {
            // Start from what "automatic" would have indexed, so the user can prune it.
            foreach (var r in FileIndex.ResolveRoots(new Core.Settings.AppSettings())) _roots.Add(r);
        }
        UpdateRootsVisibility();
        SaveRoots();
    }

    private void UpdateRootsVisibility() =>
        CustomRootsPanel.Visibility = RootsMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;

    private async void OnAddRoot(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        if (!_roots.Contains(folder.Path, StringComparer.OrdinalIgnoreCase)) _roots.Add(folder.Path);
        SaveRoots();
    }

    private void OnRemoveRoot(object sender, RoutedEventArgs e)
    {
        if (RootsList.SelectedItem is string selected) _roots.Remove(selected);
        SaveRoots();
    }

    private void SaveRoots()
    {
        if (_loading) return;
        var custom = RootsMode.SelectedIndex == 1;
        _services.ApplySettings(s => s.IndexRoots = custom ? _roots.ToList() : null);
    }

    private void OnExcludedLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var names = ExcludedBox.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.SequenceEqual(_services.Settings.Current.ExcludedNames, StringComparer.OrdinalIgnoreCase)) return;
        _services.ApplySettings(s => s.ExcludedNames = names);
    }

    private void OnReindex(object sender, RoutedEventArgs e) => _services.ReindexNow();

    private void OnIndexStatusChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateIndexStatus);

    private void UpdateIndexStatus()
    {
        var status = _services.Index.Status;
        if (!_services.Settings.Current.IndexFiles) { IndexStatusText.Text = "A pesquisa de arquivos está desativada."; return; }
        if (status.Count == 0) { IndexStatusText.Text = "Nenhuma pasta indexada ainda."; return; }

        IndexStatusText.Text = string.Join("\n", status.Select(s => s.State switch
        {
            RootState.Scanning => $"{s.Root}  —  indexando…",
            RootState.Pending => $"{s.Root}  —  aguardando",
            RootState.Failed => $"{s.Root}  —  falhou (veja o log)",
            _ => $"{s.Root}  —  {s.Entries:N0} itens" +
                 (s.Truncated ? " (limite atingido)" : "") +
                 (s.ScannedAtUtc is { } at ? $", atualizado {at.ToLocalTime():dd/MM HH:mm}" : ""),
        }));
    }

    // -------------------------------------------------------------- data

    private void OnClearHistory(object sender, RoutedEventArgs e) => _services.Usage.ClearHistory();

    private void OnOpenData(object sender, RoutedEventArgs e) => ShellLauncher.Open(AppPaths.DataDirectory);
}
