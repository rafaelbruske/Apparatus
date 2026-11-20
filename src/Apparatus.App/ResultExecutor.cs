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
    private readonly Action _openSettings;
    private readonly Action _quit;

    public ResultExecutor(AppServices services, Action openSettings, Action quit)
    {
        _services = services;
        _openSettings = openSettings;
        _quit = quit;
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
                case ResultKind.Command:
                    RunCommand(result.Payload);
                    break;
                default:
                    ShellLauncher.Open(result.Payload);
                    break;
            }

            // Calculations and web searches would only pollute the history.
            if (result.Kind is not (ResultKind.Calculator or ResultKind.Web)) _services.Search.RecordUse(query, result);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Running '{result.Title}' failed", ex);
            _services.Host.ShowBalloon("Não foi possível abrir", $"{result.Title}: {ex.Message}");
        }
    }

    private void RunCommand(string id)
    {
        switch (id)
        {
            case InternalCommands.OpenSettings: _openSettings(); break;
            case InternalCommands.Reindex: _services.ReindexNow(); break;
            case InternalCommands.ClearHistory: _services.Usage.ClearHistory(); break;
            case InternalCommands.OpenDataFolder: ShellLauncher.Open(AppPaths.DataDirectory); break;
            case InternalCommands.Quit: _quit(); break;
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
