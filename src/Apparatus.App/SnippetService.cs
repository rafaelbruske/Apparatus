using System.Collections.Concurrent;
using Apparatus.Core;
using Apparatus.Core.Snippets;
using Apparatus.Native;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace Apparatus.App;

/// <summary>
/// Expands snippet triggers while the user types in any application, and inserts snippets chosen from the
/// launcher. Three threads cooperate: the hook thread (decides, must stay fast), a worker thread (does all
/// the slow injecting, one job at a time) and the UI thread (field dialogs).
/// </summary>
public sealed class SnippetService : IDisposable
{
    private const int MaxBusyMs = 2500;          // safety: the keyboard is never held longer than this
    private const int AutoPasteThreshold = 120;  // longer text is pasted instead of typed

    private readonly AppServices _services;
    private readonly DispatcherQueue _ui;
    private readonly KeyboardHook _hook = new();
    private readonly TriggerBuffer _buffer = new();
    private readonly TextInjector _injector = new();
    private readonly BlockingCollection<Action> _jobs = new();
    private readonly Thread _worker;
    private readonly string _ownProcess = Environment.ProcessPath is { } p ? Path.GetFileNameWithoutExtension(p) : "Apparatus";

    // Hook-thread state.
    private nint _lastWindow;
    private uint _swallowVk;
    private volatile HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _expansionEnabled;

    // Shared between hook and worker threads.
    private readonly object _gate = new();
    private readonly List<KeyEvent> _pending = new();
    private bool _busy;
    private long _busyUntil;
    private volatile bool _dialogOpen;

    private nint _launcherTarget;
    private long _ownSeen;
    private volatile bool _replaying;

    public SnippetService(AppServices services, DispatcherQueue ui)
    {
        _services = services;
        _ui = ui;
        _hook.Handler = OnKey;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Snippet worker" };
        _worker.Start();

        services.Snippets.Changed += (_, _) => RefreshTriggers();
        services.Settings.Changed += (_, _) => ApplySettings();
        RefreshTriggers();
        ApplySettings();
    }

    // ------------------------------------------------------------ setup

    private void RefreshTriggers() => _buffer.SetSnippets(_services.Snippets.All);

    public void ApplySettings()
    {
        var s = _services.Settings.Current;
        _excluded = new HashSet<string>(s.SnippetExcludedProcesses.Append(_ownProcess), StringComparer.OrdinalIgnoreCase);
        _expansionEnabled = s.SnippetExpansion;
        if (s.SnippetExpansion && !_hook.IsRunning) _hook.Start();
        else if (!s.SnippetExpansion && _hook.IsRunning) _hook.Stop();
    }

    public bool IsExpansionActive => _hook.IsRunning;

    /// <summary>Called when the launcher opens, before it takes the focus: where inserted text should go.</summary>
    public void RememberLauncherTarget(nint hwnd) => _launcherTarget = hwnd;

    // ------------------------------------------------------- hook thread

    private static readonly bool DebugKeys = Environment.GetEnvironmentVariable("APPARATUS_DEBUG_KEYS") == "1";

    private bool OnKey(KeyEvent e)
    {
        if (DebugKeys && _busy)
            AppLog.Info($"[key during expansion] vk={e.VirtualKey:X2} down={e.IsDown} injected={e.IsInjected} extra={e.ExtraInfo:X}");
        if (KeyboardHook.IsOwn(e)) { Interlocked.Increment(ref _ownSeen); return false; }   // our own injected input

        // Belt and braces: while replaying held keys, nothing injected may be held again (that would loop forever).
        if (_replaying && e.IsInjected) return false;

        // Finish swallowing the key that completed a trigger (its key-up and auto-repeats).
        if (_swallowVk != 0 && e.VirtualKey == _swallowVk)
        {
            if (!e.IsDown) _swallowVk = 0;
            return true;
        }

        lock (_gate)
        {
            if (_busy && !_dialogOpen && Environment.TickCount64 < _busyUntil)
            {
                _pending.Add(e);   // hold the user's keystrokes until the expansion is done, then replay them
                return true;
            }
        }

        if (!e.IsDown) return false;
        if (!_expansionEnabled) return false;

        var window = KeyTranslator.ForegroundWindow();
        if (window != _lastWindow)
        {
            _lastWindow = window;
            _buffer.Reset();
        }

        switch (KeyTranslator.Classify(e, out string text))
        {
            case KeyAction.Reset:
                _buffer.Reset();
                return false;
            case KeyAction.Backspace:
                _buffer.Backspace();
                return false;
            case KeyAction.Text:
                var hit = _buffer.Append(text);
                if (hit is null || !MayExpandHere()) return false;

                lock (_gate)
                {
                    _busy = true;
                    _busyUntil = Environment.TickCount64 + MaxBusyMs;
                }
                _swallowVk = e.VirtualKey;
                _jobs.Add(() => RunAutoExpansion(hit, text, window));
                return true;   // the last character of the trigger never reaches the app
            default:
                return false;
        }
    }

    private bool MayExpandHere()
    {
        if (_excluded.Contains(KeyTranslator.ForegroundProcessName())) return false;
        return !KeyTranslator.IsPasswordFieldFocused();
    }

    // ---------------------------------------------------- worker thread

    private void WorkerLoop()
    {
        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            try { job(); }
            catch (Exception ex) { AppLog.Error("Snippet job failed", ex); }
        }
    }

    private void RunAutoExpansion(Snippet snippet, string lastTyped, nint target)
    {
        bool completed = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            TextInjector.WaitForModifiersReleased(400);
            AppLog.Info($"[expand {snippet.Trigger}] modifiers released after {clock.ElapsedMilliseconds} ms");
            var template = SnippetTemplate.Parse(snippet.Content);

            Dictionary<string, string>? values = null;
            if (template.Fields.Count > 0)
            {
                _dialogOpen = true;
                values = AskForFields(snippet.Name, template.Fields);
                _dialogOpen = false;
                if (values is null)
                {
                    _injector.Type(lastTyped);   // cancelled: hand back the character that completed the trigger
                    completed = true;
                    return;
                }
                RestoreFocus(target);
            }

            var rendered = template.Render(SnippetContext.Create(template.UsesClipboard ? _injector.Clipboard.GetText() : null), values);
            int erase = snippet.Trigger.Length - lastTyped.Length;
            _injector.Backspaces(erase);
            AppLog.Info($"[expand {snippet.Trigger}] erased {erase} after {clock.ElapsedMilliseconds} ms");
            Insert(rendered, snippet.Mode);
            _services.Snippets.RecordUse(snippet.Id);
            completed = true;
            AppLog.Info($"Expanded {snippet.Trigger} in {clock.ElapsedMilliseconds} ms ({rendered.Text.Length} chars, cursor {rendered.CursorFromEnd}); own key events seen by the hook: {Interlocked.Read(ref _ownSeen)}; foreground now: {KeyTranslator.ForegroundProcessName()}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Expanding {snippet.Trigger} failed", ex);
        }
        finally
        {
            _dialogOpen = false;
            if (!completed)
            {
                try { _injector.Type(lastTyped); } catch { /* best effort */ }
            }
            ReleaseKeyboard();
        }
    }

    /// <summary>Replays what the user typed meanwhile, then lets the hook stop holding keys.</summary>
    private void ReleaseKeyboard()
    {
        while (true)
        {
            KeyEvent[] batch;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _busy = false;
                    return;
                }
                batch = _pending.ToArray();
                _pending.Clear();
            }
            _replaying = true;
            try { foreach (var e in batch) TextInjector.Replay(e); }
            finally { _replaying = false; }
        }
    }

    private void Insert(RenderedSnippet rendered, InsertMode mode)
    {
        bool paste = mode == InsertMode.Paste ||
                     (mode == InsertMode.Auto && (rendered.Text.Contains('\n') || rendered.Text.Length > AutoPasteThreshold));
        if (rendered.Text.Length > 0)
        {
            if (!(paste && _injector.Paste(rendered.Text))) _injector.Type(rendered.Text);
        }
        if (rendered.CursorFromEnd > 0)
        {
            Thread.Sleep(30);
            _injector.Left(rendered.CursorFromEnd);
        }
    }

    private static void RestoreFocus(nint target)
    {
        if (target == 0) return;
        WindowHelpers.BringToForeground(target);
        var deadline = Environment.TickCount64 + 600;
        while (Environment.TickCount64 < deadline && KeyTranslator.ForegroundWindow() != target) Thread.Sleep(10);
        Thread.Sleep(40);
    }

    /// <summary>Shows the field dialog on the UI thread and blocks the worker until it closes. Null means cancelled.</summary>
    private Dictionary<string, string>? AskForFields(string title, IReadOnlyList<SnippetField> fields) =>
        AskForFieldsAsync(title, fields).GetAwaiter().GetResult();

    /// <summary>Shows the field dialog on the UI thread. Completes with null when the user cancels.</summary>
    public Task<Dictionary<string, string>?> AskForFieldsAsync(string title, IReadOnlyList<SnippetField> fields)
    {
        var done = new TaskCompletionSource<Dictionary<string, string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ui.TryEnqueue(async () =>
            {
                try { done.SetResult(await new SnippetFillWindow(title, fields).AskAsync()); }
                catch (Exception ex) { AppLog.Error("Field dialog failed", ex); done.SetResult(null); }
            }))
        {
            done.SetResult(null);
        }
        return done.Task;
    }

    // ------------------------------------------------- from the launcher

    /// <summary>Inserts (or, with <paramref name="copyOnly"/>, copies) the snippet picked in the launcher. UI thread.</summary>
    public async void RunFromLauncher(long snippetId, bool copyOnly)
    {
        try
        {
            var snippet = _services.Snippets.Get(snippetId);
            if (snippet is null) return;
            await InsertTemplateAsync(snippet.Name, snippet.Content, snippet.Mode, _launcherTarget, copyOnly);
            _services.Snippets.RecordUse(snippet.Id);
        }
        catch (OperationCanceledException)
        {
            // The user closed the fields dialog.
        }
        catch (Exception ex)
        {
            AppLog.Error("Inserting a snippet from the launcher failed", ex);
            _services.Host.ShowBalloon("Não foi possível inserir o snippet", ex.Message);
        }
    }

    /// <summary>The window that had the focus when the launcher was opened.</summary>
    public nint LauncherTarget => _launcherTarget;

    /// <summary>
    /// Renders a snippet-style template (asking for its fields first, if any) and types or pastes it into
    /// <paramref name="target"/> (the focused window when 0), or copies it when <paramref name="copyOnly"/>.
    /// Completes when the text has been delivered. Throws <see cref="OperationCanceledException"/> if the user
    /// cancels the fields dialog.
    /// </summary>
    public async Task InsertTemplateAsync(string title, string content, InsertMode mode, nint target = 0, bool copyOnly = false)
    {
        var template = SnippetTemplate.Parse(content);
        if (target == 0) target = KeyTranslator.ForegroundWindow();

        Dictionary<string, string>? values = null;
        if (template.Fields.Count > 0)
        {
            values = await AskForFieldsAsync(title, template.Fields).ConfigureAwait(false);
            if (values is null) throw new OperationCanceledException("Cancelado no diálogo de campos.");
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Add(() =>
        {
            try
            {
                var rendered = template.Render(SnippetContext.Create(template.UsesClipboard ? _injector.Clipboard.GetText() : null), values);
                if (copyOnly)
                {
                    _ui.TryEnqueue(() =>
                    {
                        var package = new DataPackage();
                        package.SetText(rendered.Text.Replace("\n", "\r\n"));
                        Clipboard.SetContent(package);
                        Clipboard.Flush();
                    });
                }
                else
                {
                    RestoreFocus(target);
                    TextInjector.WaitForModifiersReleased(400);
                    Insert(rendered, mode);
                }
                done.SetResult();
            }
            catch (Exception ex)
            {
                AppLog.Error($"Inserting '{title}' failed", ex);
                done.SetException(ex);
            }
        });
        await done.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        _hook.Dispose();
        _jobs.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(1));
        _injector.Dispose();
    }
}
