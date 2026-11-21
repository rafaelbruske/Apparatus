using Apparatus.Core;
using Apparatus.Native;
using Microsoft.UI.Dispatching;

namespace Apparatus.App;

/// <summary>
/// Owns the Start menu window and the Windows-key interceptor. The interceptor listens only while the feature is switched
/// on in the settings; switching it off gives Windows' own Start menu back immediately.
/// </summary>
public sealed class StartMenuService : IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherQueue _ui;
    private readonly WinKeyInterceptor _interceptor;
    private readonly StartMenuWindow _window;

    public StartMenuService(AppServices services, DispatcherQueue ui, ResultExecutor executor)
    {
        _services = services;
        _ui = ui;
        _window = new StartMenuWindow(services, executor);
        _interceptor = new WinKeyInterceptor(services.Keyboard);
        _interceptor.Tapped += () => _ui.TryEnqueue(Toggle);
        services.Settings.Changed += (_, _) => _ui.TryEnqueue(ApplySettings);
        ApplySettings();
    }

    public bool WinKeyActive => _interceptor.IsActive;

    public bool IsVisible => _window.IsMenuVisible;

    public void ApplySettings() => _interceptor.SetActive(_services.Settings.Current.StartMenuEnabled);

    public void Prewarm(Action? done = null) => _window.Prewarm(done);

    public void Show() => _window.ShowMenu();

    public void ShowWithQuery(string query) => _window.ShowWithQuery(query);

    public void ShowAllApps() => _window.ShowAllApps();

    public void Toggle() => _window.ToggleMenu();

    public void Hide() => _window.HideMenu();

    public void Close()
    {
        _window.AllowClose();
        _window.Close();
    }

    public void Dispose()
    {
        _interceptor.Dispose();
    }
}
