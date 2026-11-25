using Apparatus.Core;
using Apparatus.Core.Search;
using Apparatus.Core.Search.Providers;
using Apparatus.Native;
using Windows.ApplicationModel.DataTransfer;

namespace Apparatus.App;

/// <summary>Runs the action behind a launcher result.</summary>
public sealed class ResultExecutor
{
    private readonly AppServices _services;
    private readonly Action<string> _openSettings;   // the id of the settings page to show ("general", "extensions"...)
    private readonly Action _quit;
    private readonly Action<bool> _openSnippets;   // true = start a new snippet
    private readonly Action<bool> _openActions;    // true = start a new action

    private readonly Action<Apparatus.Core.Actions.ActionDefinition>? _openActionDraft;
    private readonly Action<Apparatus.Core.Snippets.Snippet>? _openSnippetDraft;

    public ResultExecutor(AppServices services, Action<string> openSettings, Action quit, Action<bool> openSnippets, Action<bool> openActions,
        Action<Apparatus.Core.Actions.ActionDefinition>? openActionDraft = null, Action<Apparatus.Core.Snippets.Snippet>? openSnippetDraft = null)
    {
        _openActionDraft = openActionDraft;
        _openSnippetDraft = openSnippetDraft;
        _openActions = openActions;
        _services = services;
        _openSettings = openSettings;
        _quit = quit;
        _openSnippets = openSnippets;
    }

    /// <param name="secondary">Ctrl+Enter: the alternative action (for files, reveal in Explorer).</param>
    public void Run(SearchResult result, string query, bool secondary)
    {
        try
        {
            switch (result.Kind)
            {
                case ResultKind.File or ResultKind.Folder when secondary:
                    ShellLauncher.RevealInExplorer(result.Payload);
                    break;
                case ResultKind.Calculator:
                    CopyToClipboard(result.Payload);
                    break;
                case ResultKind.Snippet:
                    // Enter inserts into the app that was focused before the launcher; Ctrl+Enter only copies.
                    _services.Expander?.RunFromLauncher(long.Parse(result.Payload), copyOnly: secondary);
                    break;
                case ResultKind.Extension:
                    _services.ExtensionRunner?.Run(result.Payload, secondary, _services.Expander?.LauncherTarget ?? 0);
                    break;
                case ResultKind.Clipboard:
                    _services.ClipboardHost?.Activate(long.Parse(result.Payload), secondary, _services.Expander?.LauncherTarget ?? 0);
                    break;
                case ResultKind.Action:
                    // Run in the application that had the focus before the launcher opened.
                    _services.ActionHost?.Run(long.Parse(result.Payload), _services.Expander?.LauncherTarget ?? 0);
                    break;
                case ResultKind.Command:
                    RunCommand(result.Payload);
                    break;
                default:
                    ShellLauncher.Open(result.Payload);
                    break;
            }

            // Calculations and web searches would only pollute the history.
            if (result.Kind is not (ResultKind.Calculator or ResultKind.Web or ResultKind.Clipboard)) _services.Search.RecordUse(query, result);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Running '{result.Title}' failed", ex);
            _services.Host.ShowBalloon("Não foi possível abrir", $"{result.Title}: {ex.Message}");
        }
    }

    /// <summary>
    /// Carries out a button of the assistant. Only called after the user picked it. Every kind maps to code that already exists
    /// (the same launch paths as a search result); drafts only open an editor, they never save anything.
    /// </summary>
    public void RunSuggestion(Apparatus.Core.Assistant.SuggestedAction s)
    {
        try
        {
            switch (s.Kind)
            {
                case Apparatus.Core.Assistant.SuggestionKind.OpenWindowsSetting:
                case Apparatus.Core.Assistant.SuggestionKind.OpenUrl:
                    ShellLauncher.Open(s.Payload);
                    break;
                case Apparatus.Core.Assistant.SuggestionKind.OpenApparatusScreen:
                    switch (s.Payload)
                    {
                        case "settings": _openSettings("general"); break;
                        case "extensions": _openSettings("extensions"); break;
                        case "capture": _openSettings("capture"); break;
                        case "snippets": _openSnippets(false); break;
                        case "actions": _openActions(false); break;
                    }
                    break;
                case Apparatus.Core.Assistant.SuggestionKind.RunMyAction:
                    _services.ActionHost?.Run(s.ActionId, _services.Expander?.LauncherTarget ?? 0);
                    break;
                case Apparatus.Core.Assistant.SuggestionKind.ProposeAction when s.ActionDraft is { } draft:
                    if (_openActionDraft is not null) _openActionDraft(draft); else _openActions(true);
                    break;
                case Apparatus.Core.Assistant.SuggestionKind.ProposeSnippet when s.SnippetDraft is { } snippet:
                    if (_openSnippetDraft is not null) _openSnippetDraft(snippet); else _openSnippets(true);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Running the assistant's suggestion '{s.Title}' failed", ex);
            _services.Host.ShowBalloon("Não foi possível abrir", $"{s.Title}: {ex.Message}");
        }
    }

    private void RunCommand(string id)
    {
        switch (id)
        {
            case InternalCommands.OpenSettings: _openSettings("general"); break;
            case InternalCommands.Reindex: _services.ReindexNow(); break;
            case InternalCommands.ClearHistory: _services.Usage.ClearHistory(); break;
            case InternalCommands.OpenDataFolder: ShellLauncher.Open(AppPaths.DataDirectory); break;
            case InternalCommands.Quit: _quit(); break;
            case InternalCommands.ManageSnippets: _openSnippets(false); break;
            case InternalCommands.NewSnippet: _openSnippets(true); break;
            case InternalCommands.ManageActions: _openActions(false); break;
            case InternalCommands.ClipboardEnable: _services.ApplySettings(s => s.ClipboardHistory = true); break;
            case InternalCommands.ClipboardClear: _services.ClipboardStore.Clear(); break;
            case InternalCommands.ManageExtensions: _openSettings("extensions"); break;
            case InternalCommands.NewAction: _openActions(true); break;
        }
    }

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
