using Apparatus.Core;

namespace Apparatus.Native;

/// <summary>
/// One system-wide keyboard hook shared by everyone who needs keys (snippet expansion, the Start menu's Win tap).
/// The hook thread runs only while somebody is subscribed. Subscribers run in subscription order on the hook thread;
/// the first one to return true swallows the key (the rest do not see it).
/// </summary>
public sealed class KeyboardHub : IDisposable
{
    private sealed class Subscription : IDisposable
    {
        private readonly KeyboardHub _hub;
        public readonly Func<KeyEvent, bool> Handler;

        public Subscription(KeyboardHub hub, Func<KeyEvent, bool> handler)
        {
            _hub = hub;
            Handler = handler;
        }

        public void Dispose() => _hub.Remove(this);
    }

    private readonly KeyboardHook _hook = new();
    private readonly object _gate = new();
    private volatile Subscription[] _subscribers = Array.Empty<Subscription>();

    public KeyboardHub() => _hook.Handler = Dispatch;

    public bool IsRunning => _hook.IsRunning;

    public int SubscriberCount => _subscribers.Length;

    /// <summary>Starts listening. Dispose the result to stop; the hook is removed with the last subscriber.</summary>
    public IDisposable Subscribe(Func<KeyEvent, bool> handler)
    {
        var sub = new Subscription(this, handler);
        lock (_gate)
        {
            _subscribers = _subscribers.Append(sub).ToArray();
            if (!_hook.IsRunning) _hook.Start();
        }
        return sub;
    }

    private void Remove(Subscription sub)
    {
        lock (_gate)
        {
            _subscribers = _subscribers.Where(s => s != sub).ToArray();
            if (_subscribers.Length == 0 && _hook.IsRunning) _hook.Stop();
        }
    }

    private bool Dispatch(KeyEvent e)
    {
        foreach (var sub in _subscribers)
        {
            try
            {
                if (sub.Handler(e)) return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Keyboard subscriber failed", ex);
            }
        }
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _subscribers = Array.Empty<Subscription>();
            _hook.Stop();
        }
    }
}
