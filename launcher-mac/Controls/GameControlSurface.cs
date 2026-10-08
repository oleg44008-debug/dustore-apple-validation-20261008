using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DustoreLauncherV.Mac.Services;

namespace DustoreLauncherV.Mac.Controls;

/// <summary>The macOS surface is a real nonactivating NSPanel, independent of Avalonia's NSWindow.</summary>
internal sealed class GameControlSurface : IDisposable
{
    private readonly NativeGameControlPanel? _native;
    private readonly GameControlWindow? _fallback;
    internal GameControlSurface(GameEntry entry, Func<bool, Task<bool>> stop, Action returnToLauncher, Window launcher)
    {
        Entry = entry;
        if (OperatingSystem.IsMacOS()) _native = new NativeGameControlPanel(entry, stop, returnToLauncher, launcher);
        else _fallback = new GameControlWindow(entry, stop, returnToLauncher);
    }
    internal GameEntry Entry { get; }
    internal bool IsVisible => _native?.IsVisible ?? _fallback!.IsVisible;
    internal bool IsNativePanel => _native is not null && _native.IsNativePanel;
    internal bool NativeAllSpacesApplied => _native?.NativeAllSpacesApplied ?? _fallback!.NativeAllSpacesApplied;
    internal GameWindowNativePolicy? NativePolicy => _native is not null ? _native.NativePolicy : _fallback!.NativePolicy;
    internal (bool OnActiveSpace, bool OcclusionVisible) NativeVisibility => _native?.NativeVisibility ?? _fallback!.NativeVisibility;
    internal PixelPoint Position => _native?.Position ?? _fallback!.Position;
    internal Size ClientSize => _native?.ClientSize ?? _fallback!.ClientSize;
    internal double RenderScaling => _native?.RenderScaling ?? _fallback!.RenderScaling;
    internal PixelRect? ScreenWorkingArea => _native is not null ? _native.ScreenWorkingArea : _fallback!.Screens.ScreenFromWindow(_fallback)?.WorkingArea;
    internal Rect ExitBounds => _native?.ExitBounds ?? _fallback!.ExitButton.Bounds;
    internal Rect ReturnBounds => _native?.ReturnBounds ?? _fallback!.ReturnButton.Bounds;
    internal string ExitName => _native?.ExitName ?? Avalonia.Automation.AutomationProperties.GetName(_fallback!.ExitButton) ?? "";
    internal string ReturnName => _native?.ReturnName ?? Avalonia.Automation.AutomationProperties.GetName(_fallback!.ReturnButton) ?? "";
    internal bool ExitHittable => _native?.ExitHittable ?? _fallback!.ExitButton.IsVisible;
    internal bool ReturnHittable => _native?.ReturnHittable ?? _fallback!.ReturnButton.IsVisible;
    internal long NativeWindowNumber => _native?.WindowNumber ?? 0;
    internal bool NativeMovable => _native?.Movable ?? false;
    internal Rect NativeScreenFrame => _native?.NativeScreenFrame ?? default;
    internal Window? FallbackWindow => _fallback;
    internal void Show() { if (_native is not null) _native.Show(); else _fallback!.Show(); }
    internal void Close() => Dispose();
    internal void UpdateLayout() { if (_native is null) _fallback!.UpdateLayout(); }
    internal void PlaceInside(PixelRect area, double scaling)
    { if (_native is not null) _native.PlaceInsideWorkingArea(); else _fallback!.PlaceInside(area, scaling); }
    internal void PerformReturn()
    { if (_native is not null) _native.PerformReturn(); else _fallback!.ReturnButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    internal void PerformExit()
    { if (_native is not null) _native.PerformExit(); else _fallback!.ExitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    internal void SendExitKey(bool commandW)
    {
        if (_native is not null) _native.SendExitKey(commandW);
        else _fallback!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent,
            Key = commandW ? Key.W : Key.Escape, KeyModifiers = commandW ? KeyModifiers.Meta : KeyModifiers.None });
    }
    internal Task RequestStopAsync() => _native?.RequestStopAsync() ?? _fallback!.RequestStopAsync();
    public void Dispose() { if (_native is not null) _native.Dispose(); else _fallback!.Close(); }
}
