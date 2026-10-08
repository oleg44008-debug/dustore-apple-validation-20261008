using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace DustoreLauncherV.Mac.Controls;

/// <summary>A cancellable, short arrival animation. No polling continues after it finishes.</summary>
internal sealed class SurfaceMotion : IDisposable
{
    private readonly Dictionary<Control, DispatcherTimer> _timers = new();
    private bool _disposed;

    public void Reveal(Control? surface, bool reducedMotion, int milliseconds = 260, double distance = 12)
    {
        if (surface is null || _disposed) return;
        if (_timers.Remove(surface, out var old)) old.Stop();
        surface.Opacity = 1;
        surface.RenderTransform = null;
        if (reducedMotion || !surface.IsVisible) return;
        var translation = new TranslateTransform(0, distance);
        surface.RenderTransform = translation;
        surface.Opacity = 0;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var easing = new CubicEaseOut();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16.7) };
        timer.Tick += (_, _) =>
        {
            double progress = Math.Clamp(elapsed.Elapsed.TotalMilliseconds / milliseconds, 0, 1);
            double eased = easing.Ease(progress);
            surface.Opacity = eased;
            translation.Y = distance * (1 - eased);
            if (progress < 1 && surface.IsVisible && !_disposed) return;
            timer.Stop();
            _timers.Remove(surface);
            surface.Opacity = 1;
            surface.RenderTransform = null;
        };
        _timers[surface] = timer;
        timer.Start();
    }

    public void Reset()
    {
        foreach (var (surface, timer) in _timers)
        {
            timer.Stop(); surface.Opacity = 1; surface.RenderTransform = null;
        }
        _timers.Clear();
    }

    public void Dispose() { Reset(); _disposed = true; }
}
