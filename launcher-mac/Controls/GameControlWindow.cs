using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DustoreLauncherV.Mac.Services;

namespace DustoreLauncherV.Mac.Controls;

/// <summary>A small unowned game controller: launcher minimization never hides its Exit button.</summary>
internal sealed class GameControlWindow : Window
{
    private readonly Func<bool, Task<bool>> _stop;
    private readonly Action _return;
    private readonly TextBlock _status;
    private bool _force, _stopping;
    internal Button ExitButton { get; }
    internal Button ReturnButton { get; }
    internal bool NativeAllSpacesApplied { get; private set; }
    internal (bool OnActiveSpace, bool OcclusionVisible) NativeVisibility => NativeGameWindow.Visibility(this);
    internal GameEntry Entry { get; }

    internal GameControlWindow(GameEntry entry, Func<bool, Task<bool>> stop, Action returnToLauncher)
    {
        Entry = entry; _stop = stop; _return = returnToLauncher;
        Title = "DUSTORE · " + entry.Name;
        Width = 340; Height = 108; MinWidth = 220; MinHeight = 100;
        CanResize = false; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        SystemDecorations = SystemDecorations.None; WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(Color.Parse("#17151F"));
        var stack = new StackPanel { Spacing = 6, Margin = new Thickness(12) };
        var title = new TextBlock { Text = entry.Name, FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
        stack.Children.Add(title);
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        ReturnButton = new Button { Content = "Лаунчер", HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 6) };
        AutomationProperties.SetName(ReturnButton, "Вернуться в лаунчер из игры");
        ExitButton = new Button { Content = "Выйти из игры", HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 6), Background = new SolidColorBrush(Color.Parse("#DDFC85")), Foreground = new SolidColorBrush(Color.Parse("#142006")) };
        AutomationProperties.SetName(ExitButton, "Закрыть запущенную игру " + entry.Name);
        Grid.SetColumn(ExitButton, 1); actions.Children.Add(ReturnButton); actions.Children.Add(ExitButton); stack.Children.Add(actions);
        _status = new TextBlock { Text = "Возврат и выход всегда рядом", FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse("#CAC4D9")), TextTrimming = TextTrimming.CharacterEllipsis };
        stack.Children.Add(_status);
        Content = new Border { Child = stack, BorderBrush = new SolidColorBrush(Color.Parse("#71677F")), BorderThickness = new Thickness(1) };
        ReturnButton.Click += (_, _) => _return();
        ExitButton.Click += async (_, _) => await RequestStopAsync();
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape || e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            { e.Handled = true; await RequestStopAsync(); }
        };
        Opened += (_, _) => { PlaceInsideWorkingArea(); NativeAllSpacesApplied = NativeGameWindow.MakeAuxiliary(this); };
    }

    internal void PlaceInsideWorkingArea()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen) return;
        PlaceInside(screen.WorkingArea, screen.Scaling);
    }

    internal void PlaceInside(PixelRect area, double scaling)
    {
        scaling = double.IsFinite(scaling) && scaling > 0 ? scaling : 1;
        double available = Math.Max(120, area.Width / scaling - 24);
        MinWidth = Math.Min(220, available); Width = Math.Min(340, available);
        Height = Math.Min(108, Math.Max(60, area.Height / scaling - 24)); MinHeight = Math.Min(100, Height);
        int margin = (int)Math.Round(12 * scaling);
        Position = new PixelPoint(Math.Max(area.X, area.Right - (int)Math.Ceiling(Width * scaling) - margin), area.Y + margin);
    }

    internal async Task RequestStopAsync()
    {
        if (_stopping) return;
        _stopping = true; ExitButton.IsEnabled = false; _status.Text = "Закрываю игру…";
        try
        {
            bool finished = await _stop(_force);
            if (finished) Close();
            else
            {
                _force = true; ExitButton.Content = "Принудительно";
                AutomationProperties.SetName(ExitButton, "Принудительно закрыть только игру " + Entry.Name);
                _status.Text = "Игра ещё работает. Можно завершить принудительно.";
            }
        }
        catch (Exception error)
        {
            _force = true; ExitButton.Content = "Принудительно"; _status.Text = error.Message;
            ToolTip.SetTip(_status, error.Message);
        }
        finally { _stopping = false; ExitButton.IsEnabled = true; }
    }

    private static class NativeGameWindow
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        [DllImport(ObjC)] private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nuint Get(IntPtr instance, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void Set(IntPtr instance, IntPtr selector, nuint value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")][return: MarshalAs(UnmanagedType.I1)]
        private static extern bool Boolean(IntPtr instance, IntPtr selector);
        internal static (bool OnActiveSpace, bool OcclusionVisible) Visibility(Window window)
        {
            if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle() is not { HandleDescriptor: "NSWindow" } handle || handle.Handle == IntPtr.Zero)
                return (false, false);
            return (Boolean(handle.Handle, sel_registerName("isOnActiveSpace")), (Get(handle.Handle, sel_registerName("occlusionState")) & 2) != 0);
        }
        internal static bool MakeAuxiliary(Window window)
        {
            if (!OperatingSystem.IsMacOS()) return false;
            Dispatcher.UIThread.VerifyAccess();
            // Avalonia 11.3.21 explicitly exposes its native NSWindow through this public handle.
            if (window.TryGetPlatformHandle() is not { HandleDescriptor: "NSWindow" } handle || handle.Handle == IntPtr.Zero) return false;
            nuint old = Get(handle.Handle, sel_registerName("collectionBehavior"));
            const nuint allSpaces = 1, fullScreenPrimary = 1 << 7, fullScreenAuxiliary = 1 << 8;
            nuint next = (old | allSpaces | fullScreenAuxiliary) & ~fullScreenPrimary;
            Set(handle.Handle, sel_registerName("setCollectionBehavior:"), next);
            return (Get(handle.Handle, sel_registerName("collectionBehavior")) & (allSpaces | fullScreenAuxiliary)) == (allSpaces | fullScreenAuxiliary);
        }
    }
}
