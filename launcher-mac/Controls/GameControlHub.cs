using Avalonia.Controls;
using Avalonia.Threading;
using DustoreLauncherV.Mac.Services;

namespace DustoreLauncherV.Mac.Controls;

/// <summary>Owns only launcher game controls; it never owns or closes unrelated game windows.</summary>
internal sealed class GameControlHub : IDisposable
{
    private readonly MainWindow _launcher;
    private readonly Dictionary<Guid, GameControlSurface> _windows = new();
    private bool _disposed;
    internal GameControlHub(MainWindow launcher)
    {
        _launcher = launcher;
        GameSessions.Changed += Changed;
        NativeAppSessions.Changed += Changed;
    }
    internal GameControlSurface? Find(Guid id) => _windows.GetValueOrDefault(id);
    private void Changed(object? sender, GameSessionEventArgs args) => Dispatcher.UIThread.Post(Refresh);
    private void Refresh()
    {
        if (_disposed || !_launcher.IsVisible) return;
        var native = NativeAppSessions.Snapshot(); var wine = GameSessions.Snapshot();
        var ids = native.Select(s => s.Entry.Id).Concat(wine.Select(s => s.Entry.Id)).ToHashSet();
        foreach (var id in _windows.Keys.Where(id => !ids.Contains(id) || !_windows[id].IsVisible).ToArray())
        { _windows[id].Close(); _windows.Remove(id); }
        foreach (var session in native)
            Add(session.Entry, force => session.StopAsync(force, CancellationToken.None));
        foreach (var session in wine)
            Add(session.Entry, async _ =>
            {
                await session.StopAsync(CancellationToken.None);
                for (int i = 0; i < 20 && GameSessions.Find(session.Entry.Id) is not null; i++) await Task.Delay(100);
                return GameSessions.Find(session.Entry.Id) is null;
            });
    }
    private void Add(GameEntry entry, Func<bool, Task<bool>> stop)
    {
        if (_windows.ContainsKey(entry.Id)) return;
        var panel = new GameControlSurface(entry, stop, () => ReturnToLauncher(entry.Id), _launcher);
        _windows.Add(entry.Id, panel);
        // A parented tool window is hidden when ULTRA minimizes the launcher. This is unowned.
        panel.Show();
        if (_launcher.Screens.ScreenFromWindow(_launcher) is { } screen) panel.PlaceInside(screen.WorkingArea, screen.Scaling);
    }
    internal void ReturnToLauncher(Guid id)
    {
        if (_disposed) return;
        if (_launcher.WindowState == WindowState.Minimized) _launcher.WindowState = WindowState.Normal;
        _launcher.Show(); _launcher.Activate();
        _launcher.ViewModel.ShowRunningGame(id);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        GameSessions.Changed -= Changed; NativeAppSessions.Changed -= Changed;
        foreach (var panel in _windows.Values) panel.Close();
        _windows.Clear();
    }
}
