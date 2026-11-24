using System.Collections.Specialized;
using Apparatus.Core.Assistant;
using Apparatus.Core.Search;
using Apparatus.Core.Search.Providers;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Apparatus.App;

/// <summary>
/// The launcher's AI mode. Tab on an empty box (or when nothing matched) swaps the result list for a conversation;
/// the same text box stays the input. Enter sends, Esc cancels, then clears, then leaves the mode, then closes the launcher.
/// </summary>
public sealed partial class LauncherWindow
{
    private const string DefaultPlaceholder = "Pesquisar apps, arquivos, configurações…";
    private const string SearchGlyph = "";
    private const string ChatGlyph = "";

    /// <summary>Height of the conversation area, in DIPs. The window has this fixed height while in AI mode.</summary>
    private const double ChatDip = 380;

    private bool _aiMode;
    private bool _isActive;   // the window has the focus (kept by OnActivated)

    private AssistantChat Chat => _services.Assistant;

    private bool AssistantEnabled => _services.Settings.Current.AssistantEnabled;

    private void HookAssistant()
    {
        ChatItems.ItemsSource = Chat.Entries;
        Chat.Entries.CollectionChanged += OnChatEntriesChanged;
        Chat.BusyChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateAiFooter);
    }

    // ------------------------------------------------------------------------------------------ mode

    /// <summary>Tab opens the AI mode when the box is empty or nothing matched; otherwise Tab keeps moving through the results.</summary>
    private bool CanEnterAiMode() => AssistantEnabled && (SearchBox.Text.Length == 0 || Items.Count == 0);

    private void EnterAiMode()
    {
        if (_aiMode || !AssistantEnabled) return;
        _aiMode = true;
        _searchCts?.Cancel();

        Divider.Visibility = Visibility.Visible;
        ResultsList.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Visible;
        Footer.Visibility = Visibility.Visible;
        SearchIcon.Glyph = ChatGlyph;
        CloseChatButton.Visibility = Visibility.Visible;
        UpdateSearchPlaceholder();
        UpdateChatEmpty();
        UpdateAiFooter();
        ResizeToContent();
        ScrollChatToEnd();
    }

    private void ExitAiMode()
    {
        if (!_aiMode) return;
        LeaveAiVisuals();
        _ = RunSearchAsync(SearchBox.Text);   // brings the list (and its size) back
    }

    /// <summary>Puts the window back in search mode without searching (used when the launcher is shown afresh).</summary>
    private void LeaveAiVisuals()
    {
        _aiMode = false;
        ChatPanel.Visibility = Visibility.Collapsed;
        CloseChatButton.Visibility = Visibility.Collapsed;
        SearchIcon.Glyph = SearchGlyph;
        UpdateSearchPlaceholder();
        UpdateHints();
        UpdateIndexStatus();
    }

    private void UpdateSearchPlaceholder() =>
        SearchBox.PlaceholderText = _aiMode ? "Pergunte algo… (Enter envia)"
            : AssistantEnabled ? "Pesquisar… ou pressione Tab para perguntar à IA"
            : DefaultPlaceholder;

    private void UpdateChatEmpty()
    {
        bool empty = Chat.Entries.Count == 0;
        ChatEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        ChatEmpty.Text = _services.AssistantMissing is { } missing
            ? $"O assistente ainda não está pronto.\n{missing}\n\nCtrl+, abre as configurações."
            : "Pergunte algo sobre o Windows ou o Apparatus.\nPor exemplo: “como altero as configurações de impressoras?”";
    }

    /// <summary>True when Enter (with an empty box) would run a button of the assistant instead of sending a question.</summary>
    private bool EnterRunsSuggestion => !Chat.IsBusy && SearchBox.Text.Trim().Length == 0 && Chat.SelectedSuggestion is not null;

    private void UpdateAiFooter()
    {
        if (!_aiMode) return;
        if (Chat.IsBusy)
        {
            HintsText.Text = "Esc Cancelar";
            StatusText.Text = "Pensando…";
            return;
        }
        if (EnterRunsSuggestion)
        {
            HintsText.Text = Chat.Suggestions.Count > 1
                ? "↵ Executar     ↑↓ Escolher     Digite para perguntar     Tab Voltar"
                : "↵ Executar     Digite para perguntar     Ctrl+N Nova conversa     Tab Voltar";
        }
        else
        {
            HintsText.Text = _services.AssistantMissing is null
                ? "↵ Enviar     Ctrl+N Nova conversa     Ctrl+Shift+C Copiar resposta     Tab Voltar"
                : "↵ Enviar     Ctrl+N Nova conversa     Ctrl+, Configurar     Tab Voltar";
        }
        StatusText.Text = _services.AssistantDescription();
    }

    // ------------------------------------------------------------------------------------------ buttons

    private void OnCloseChatClick(object sender, RoutedEventArgs e) => HideLauncher();

    private void OnSuggestionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatEntry entry }) RunSuggestion(entry);
    }

    /// <summary>Carries out a button the user picked (click, or Enter on the selected one). Nothing the assistant offers runs before this.</summary>
    private void RunSuggestion(ChatEntry entry)
    {
        if (entry.Suggestion is not { } s || Chat.IsBusy) return;
        Chat.SelectSuggestion(entry);

        if (s.Kind == Apparatus.Core.Assistant.SuggestionKind.OpenLauncher)
        {
            // The launcher is already open: leave the conversation and search for the text.
            SearchBox.Text = s.Payload;
            ExitAiMode();
            SearchBox.Select(SearchBox.Text.Length, 0);
            return;
        }

        HideLauncher();   // hide first so the opened window gets the foreground
        _executor.RunSuggestion(s);
    }

    private void MoveSuggestion(int delta)
    {
        if (Chat.MoveSuggestion(delta) is not { } entry) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            (ChatItems.ContainerFromItem(entry) as FrameworkElement)?.StartBringIntoView());
    }

    // ------------------------------------------------------------------------------------------ conversation

    private void OnChatEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (ChatEntry entry in e.NewItems) entry.PropertyChanged += (_, a) =>
            {
                if (a.PropertyName == nameof(ChatEntry.Text)) ScrollChatToEnd();   // selecting a button must not jump to the end
            };
        UpdateChatEmpty();
        UpdateAiFooter();
        ScrollChatToEnd();
    }

    private void ScrollChatToEnd() =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ChatScroll.UpdateLayout();
            ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null, disableAnimation: true);
        });

    private async Task SendQuestionAsync()
    {
        if (Chat.IsBusy) return;
        string question = SearchBox.Text.Trim();
        if (question.Length == 0) return;

        SearchBox.Text = "";
        var outcome = await Chat.AskAsync(question);

        // A cancelled or failed question goes back into the box, so it can be edited and sent again.
        if (outcome is AskOutcome.Cancelled or AskOutcome.Failed && SearchBox.Text.Length == 0)
        {
            SearchBox.Text = question;
            SearchBox.Select(question.Length, 0);
        }
        UpdateChatEmpty();
        UpdateAiFooter();
    }

    private void CopyLastAnswer()
    {
        var last = Chat.Entries.LastOrDefault(e => e.Role == ChatEntryRole.Assistant && e.Text.Length > 0);
        if (last is null) return;
        var package = new DataPackage();
        package.SetText(last.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        StatusText.Text = "Resposta copiada";
    }

    private void OpenAssistantSettings()
    {
        HideLauncher();
        _executor.Run(new SearchResult("command:settings", ResultKind.Command, "Configurações", "", InternalCommands.OpenSettings), "", secondary: false);
    }

    // ------------------------------------------------------------------------------------------ keyboard

    private void OnAiKeyDown(Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e, bool ctrl, bool shift)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                if (EnterRunsSuggestion) RunSuggestion(Chat.SelectedSuggestion!);
                else _ = SendQuestionAsync();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                if (Chat.IsBusy) Chat.CancelCurrent();
                else if (SearchBox.Text.Length > 0) SearchBox.Text = "";
                else ExitAiMode();
                e.Handled = true;
                break;
            case VirtualKey.Tab:
                if (!Chat.IsBusy && SearchBox.Text.Length == 0) ExitAiMode();
                e.Handled = true;   // Tab never moves focus out of the box
                break;
            case VirtualKey.N when ctrl && !Chat.IsBusy:
                Chat.Reset();
                UpdateChatEmpty();
                e.Handled = true;
                break;
            case VirtualKey.C when ctrl && shift:
                CopyLastAnswer();
                e.Handled = true;
                break;
            case (VirtualKey)188 when ctrl:   // Ctrl+,
                OpenAssistantSettings();
                e.Handled = true;
                break;
            case VirtualKey.Up:
                if (EnterRunsSuggestion && Chat.Suggestions.Count > 1) MoveSuggestion(-1);
                else ChatScroll.ChangeView(null, Math.Max(0, ChatScroll.VerticalOffset - 80), null, disableAnimation: true);
                e.Handled = true;
                break;
            case VirtualKey.Down:
                if (EnterRunsSuggestion && Chat.Suggestions.Count > 1) MoveSuggestion(1);
                else ChatScroll.ChangeView(null, ChatScroll.VerticalOffset + 80, null, disableAnimation: true);
                e.Handled = true;
                break;
            case VirtualKey.PageUp:
                ChatScroll.ChangeView(null, Math.Max(0, ChatScroll.VerticalOffset - ChatScroll.ViewportHeight * 0.9), null, disableAnimation: true);
                e.Handled = true;
                break;
            case VirtualKey.PageDown:
                ChatScroll.ChangeView(null, ChatScroll.VerticalOffset + ChatScroll.ViewportHeight * 0.9, null, disableAnimation: true);
                e.Handled = true;
                break;
        }
    }
}
