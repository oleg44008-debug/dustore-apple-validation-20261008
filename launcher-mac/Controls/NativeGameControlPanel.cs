using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DustoreLauncherV.Mac.Services;

namespace DustoreLauncherV.Mac.Controls;

/// <summary>
/// A genuinely nonactivating AppKit panel. NSPanel is required for an overlay belonging to
/// a different application than the fullscreen game; changing an ordinary NSWindow's flags
/// is insufficient. Every AppKit object and action below belongs to this one game controller.
/// </summary>
internal sealed class NativeGameControlPanel : IDisposable
{
    private readonly Func<bool, Task<bool>> _stop;
    private readonly Action _return;
    private readonly IntPtr _panel;
    private IntPtr _exit, _back, _status;
    private bool _closed, _stopping, _force;
    private const double ContentWidth = 340, ContentHeight = 72;
    private static readonly Dictionary<IntPtr, NativeGameControlPanel> Instances = new();
    private static readonly ActionCallback ReturnCallback = ReturnAction;
    private static readonly ActionCallback ExitCallback = ExitAction;
    private static readonly ActionCallback KeyCallback = KeyAction;
    private static readonly EventCallback EquivalentCallback = KeyEquivalent;
    private static readonly BooleanCallback KeyWindowCallback = (_, _) => 1;
    private static readonly BooleanCallback MainWindowCallback = (_, _) => 0;
    private static IntPtr _panelClass;

    internal NativeGameControlPanel(GameEntry entry, Func<bool, Task<bool>> stop, Action returnToLauncher, Window launcher)
    {
        Dispatcher.UIThread.VerifyAccess();
        Entry = entry; _stop = stop; _return = returnToLauncher;
        EnsureClass();
        // Titled supplies AppKit's actual draggable title area. Nonactivating is set at creation,
        // because changing this style after creation does not establish AppKit's focus policy.
        _panel = Native.InitPanel(Native.Pointer(_panelClass, "alloc"), Native.Sel("initWithContentRect:styleMask:backing:defer:"),
            new NativeRect(0, 0, ContentWidth, ContentHeight), 1 | (1 << 7), 2, 0);
        if (_panel == IntPtr.Zero) throw new InvalidOperationException("Не удалось создать игровую панель macOS.");
        Instances.Add(_panel, this);
        try
        {
            Native.Bool(_panel, "setReleasedWhenClosed:", false);
            Native.Bool(_panel, "setFloatingPanel:", true);
            Native.Bool(_panel, "setHidesOnDeactivate:", false);
            Native.Bool(_panel, "setBecomesKeyOnlyIfNeeded:", false);
            Native.Bool(_panel, "setMovable:", true);
            Native.Bool(_panel, "setMovableByWindowBackground:", true);
            Native.Bool(_panel, "setHasShadow:", true);
            Native.Integer(_panel, "setLevel:", 3); // NSFloatingWindowLevel, without elevating above system UI.
            Native.Text(_panel, "setTitle:", "DUSTORE · " + entry.Name);
            var appearance = Native.Pointer(Native.Class("NSAppearance"), "appearanceNamed:", Native.String("NSAppearanceNameDarkAqua"));
            if (appearance != IntPtr.Zero) Native.PointerSet(_panel, "setAppearance:", appearance);
            Native.PointerSet(_panel, "setBackgroundColor:", Native.Color(0.09, 0.08, 0.12));
            var content = Native.Pointer(_panel, "contentView");
            _back = CreateButton(content, new NativeRect(12, 28, 154, 32), "Лаунчер", "Вернуться в лаунчер из игры", "dustoreReturn:");
            _exit = CreateButton(content, new NativeRect(174, 28, 154, 32), "Выйти из игры", "Закрыть запущенную игру " + entry.Name, "dustoreExit:");
            if (Native.Responds(_exit, "setBezelColor:")) Native.PointerSet(_exit, "setBezelColor:", Native.Color(0.78, 0.90, 0.42));
            _status = Native.InitFrame(Native.Pointer(Native.Class("NSTextField"), "alloc"), Native.Sel("initWithFrame:"), new NativeRect(12, 8, 316, 16));
            Native.Bool(_status, "setEditable:", false); Native.Bool(_status, "setSelectable:", false);
            Native.Bool(_status, "setBezeled:", false); Native.Bool(_status, "setDrawsBackground:", false);
            Native.PointerSet(_status, "setTextColor:", Native.Color(0.79, 0.76, 0.85));
            Native.PointerSet(_status, "setFont:", Native.Font(10));
            Native.Text(_status, "setStringValue:", "Перетащите панель за заголовок · ⌘W / Esc");
            Native.PointerSet(content, "addSubview:", _status); Native.Void(_status, "release");
            NativePolicy = ApplyPolicy();
            // The launcher's native screen is authoritative, including Retina's point coordinates.
            IntPtr screen = launcher.TryGetPlatformHandle() is { HandleDescriptor: "NSWindow" } handle
                ? Native.Pointer(handle.Handle, "screen") : IntPtr.Zero;
            PlaceInsideWorkingArea(screen);
        }
        catch { Dispose(); throw; }
    }

    internal GameEntry Entry { get; }
    internal GameWindowNativePolicy NativePolicy { get; }
    internal bool IsNativePanel => !_closed && Native.KindOf(_panel, Native.Class("NSPanel"));
    internal bool NativeAllSpacesApplied => (NativePolicy.CollectionBehavior & 257) == 257;
    internal bool IsVisible => !_closed && Native.Boolean(_panel, "isVisible");
    internal bool Movable => !_closed && Native.Boolean(_panel, "isMovable") && Native.Boolean(_panel, "isMovableByWindowBackground");
    internal (bool OnActiveSpace, bool OcclusionVisible) NativeVisibility => _closed ? (false, false)
        : (Native.Boolean(_panel, "isOnActiveSpace"), (Native.Unsigned(_panel, "occlusionState") & 2) != 0);
    internal long WindowNumber => _closed ? 0 : unchecked((long)Native.Unsigned(_panel, "windowNumber"));
    internal Rect NativeScreenFrame
    {
        get { var frame = _closed ? default : Native.Rect(_panel, "frame"); return new(frame.X, frame.Y, frame.Width, frame.Height); }
    }
    internal double RenderScaling => _closed ? 1 : Native.Double(_panel, "backingScaleFactor");
    internal Size ClientSize => _closed ? default : Native.Rect(Native.Pointer(_panel, "contentView"), "bounds").Size;
    internal Rect ExitBounds => ButtonBounds(_exit);
    internal Rect ReturnBounds => ButtonBounds(_back);
    internal string ExitName => _closed ? "" : Native.GetText(_exit, "accessibilityLabel");
    internal string ReturnName => _closed ? "" : Native.GetText(_back, "accessibilityLabel");
    internal bool ExitHittable => ButtonHittable(_exit);
    internal bool ReturnHittable => ButtonHittable(_back);
    internal PixelPoint Position
    {
        get
        {
            if (_closed) return default;
            var frame = Native.Rect(_panel, "frame"); double scale = RenderScaling;
            return new((int)Math.Round(frame.X * scale), (int)Math.Round((Native.PrimaryTop - frame.Y - frame.Height) * scale));
        }
    }
    internal PixelRect? ScreenWorkingArea
    {
        get
        {
            if (_closed) return null;
            var screen = Native.Pointer(_panel, "screen"); if (screen == IntPtr.Zero) return null;
            var frame = Native.Rect(screen, "visibleFrame"); double scale = Native.Double(screen, "backingScaleFactor");
            return new PixelRect((int)Math.Round(frame.X * scale), (int)Math.Round((Native.PrimaryTop - frame.Y - frame.Height) * scale),
                (int)Math.Round(frame.Width * scale), (int)Math.Round(frame.Height * scale));
        }
    }
    internal void Show() { Dispatcher.UIThread.VerifyAccess(); if (!_closed) Native.Void(_panel, "orderFrontRegardless"); }
    internal void PlaceInsideWorkingArea(IntPtr screen = default)
    {
        Dispatcher.UIThread.VerifyAccess(); if (_closed) return;
        if (screen == IntPtr.Zero) screen = Native.Pointer(_panel, "screen");
        if (screen == IntPtr.Zero) screen = Native.Pointer(Native.Class("NSScreen"), "mainScreen");
        if (screen == IntPtr.Zero) return;
        var area = Native.Rect(screen, "visibleFrame"); var frame = Native.Rect(_panel, "frame");
        // Centering avoids hiding the game's corner labels. Users can move the real title bar.
        var point = new NativePoint(area.X + Math.Max(0, (area.Width - frame.Width) / 2), area.Y + area.Height - 8);
        Native.PointSet(_panel, Native.Sel("setFrameTopLeftPoint:"), point);
    }
    internal void PerformReturn() { if (!_closed) Native.PointerSet(_back, "performClick:", IntPtr.Zero); }
    internal void PerformExit() { if (!_closed) Native.PointerSet(_exit, "performClick:", IntPtr.Zero); }
    internal void SendExitKey(bool commandW)
    {
        if (_closed) return;
        Native.Void(_panel, "makeKeyWindow");
        if (!Native.Boolean(_panel, "isKeyWindow")) throw new InvalidOperationException("Игровая панель macOS не получила фокус клавиатуры.");
        IntPtr characters = Native.String(commandW ? "w" : "\u001b");
        IntPtr key = Native.KeyEvent(Native.Class("NSEvent"), Native.Sel("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:"),
            10, default, commandW ? (nuint)(1 << 20) : 0, 0, (nint)WindowNumber, IntPtr.Zero, characters, characters, 0, commandW ? (ushort)13 : (ushort)53);
        if (key == IntPtr.Zero) throw new InvalidOperationException("macOS не создала событие клавиши игровой панели.");
        Native.EventBoolean(_panel, Native.Sel("performKeyEquivalent:"), key);
    }
    internal async Task RequestStopAsync()
    {
        if (_closed || _stopping) return;
        _stopping = true; Native.Bool(_exit, "setEnabled:", false); SetStatus("Закрываю игру…");
        try
        {
            bool finished = await _stop(_force);
            if (_closed) return;
            if (finished) Dispose();
            else { _force = true; SetForce(); SetStatus("Игра ещё работает. Можно завершить принудительно."); }
        }
        catch (Exception error) { if (!_closed) { _force = true; SetForce(); SetStatus(error.Message); } }
        finally { _stopping = false; if (!_closed) Native.Bool(_exit, "setEnabled:", true); }
    }
    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess(); if (_closed) return;
        _closed = true; Instances.Remove(_panel);
        Native.PointerSet(_panel, "orderOut:", IntPtr.Zero); Native.Void(_panel, "close"); Native.Void(_panel, "release");
        _exit = _back = _status = IntPtr.Zero;
    }
    private void SetForce()
    { Native.Text(_exit, "setTitle:", "Принудительно"); Native.Text(_exit, "setAccessibilityLabel:", "Принудительно закрыть только игру " + Entry.Name); }
    private void SetStatus(string text)
    { Native.Text(_status, "setStringValue:", text); Native.Text(_status, "setToolTip:", text); }
    private Rect ButtonBounds(IntPtr button)
    {
        if (_closed || button == IntPtr.Zero) return default;
        var frame = Native.Rect(button, "frame"); return new(frame.X, ClientSize.Height - frame.Y - frame.Height, frame.Width, frame.Height);
    }
    private bool ButtonHittable(IntPtr button)
    {
        if (_closed || !IsVisible || button == IntPtr.Zero || !Native.Boolean(button, "isEnabled") || Native.Boolean(button, "isHidden")) return false;
        var content = Native.Pointer(_panel, "contentView"); var frame = Native.Rect(button, "frame"); var bounds = Native.Rect(content, "bounds");
        return frame.Width >= 60 && frame.Height >= 24 && frame.X >= bounds.X && frame.Y >= bounds.Y
            && frame.X + frame.Width <= bounds.X + bounds.Width && frame.Y + frame.Height <= bounds.Y + bounds.Height
            && Native.HitTest(content, Native.Sel("hitTest:"), new NativePoint(frame.X + frame.Width / 2, frame.Y + frame.Height / 2)) == button;
    }
    private IntPtr CreateButton(IntPtr content, NativeRect frame, string title, string name, string action)
    {
        var button = Native.InitFrame(Native.Pointer(Native.Class("NSButton"), "alloc"), Native.Sel("initWithFrame:"), frame);
        if (button == IntPtr.Zero) throw new InvalidOperationException("macOS не создала кнопку игровой панели.");
        Native.Text(button, "setTitle:", title); Native.Text(button, "setAccessibilityLabel:", name);
        Native.Integer(button, "setBezelStyle:", 1); Native.Integer(button, "setButtonType:", 7);
        Native.PointerSet(button, "setFont:", Native.Font(12));
        Native.PointerSet(button, "setTarget:", _panel); Native.PointerSet(button, "setAction:", Native.Sel(action));
        Native.PointerSet(content, "addSubview:", button); Native.Void(button, "release"); return button;
    }
    private GameWindowNativePolicy ApplyPolicy()
    {
        nuint previous = Native.Unsigned(_panel, "collectionBehavior");
        nuint next = (previous | 1 | 256) & ~(nuint)(2 | 128 | 512 | 65536 | 131072);
        if (OperatingSystem.IsMacOSVersionAtLeast(13)) next |= 262144;
        Native.Integer(_panel, "setCollectionBehavior:", next);
        nuint actual = Native.Unsigned(_panel, "collectionBehavior");
        return new((ulong)previous, (ulong)actual, unchecked((long)Native.Unsigned(_panel, "level")),
            (ulong)Native.Unsigned(_panel, "styleMask"), Native.ClassName(_panel), (actual & 262144) != 0);
    }
    private static void EnsureClass()
    {
        if (_panelClass != IntPtr.Zero) return;
        _panelClass = Native.objc_allocateClassPair(Native.Class("NSPanel"), "DUSTOREGameControlPanel_v540", 0);
        if (_panelClass == IntPtr.Zero) throw new InvalidOperationException("Не удалось зарегистрировать игровую панель AppKit.");
        Native.AddMethod(_panelClass, "dustoreReturn:", ReturnCallback, "v@:@");
        Native.AddMethod(_panelClass, "dustoreExit:", ExitCallback, "v@:@");
        Native.AddMethod(_panelClass, "keyDown:", KeyCallback, "v@:@");
        string boolean = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B" : "c";
        Native.AddMethod(_panelClass, "performKeyEquivalent:", EquivalentCallback, boolean + "@:@");
        Native.AddMethod(_panelClass, "canBecomeKeyWindow", KeyWindowCallback, boolean + "@:");
        Native.AddMethod(_panelClass, "canBecomeMainWindow", MainWindowCallback, boolean + "@:");
        Native.objc_registerClassPair(_panelClass);
    }
    private static void ReturnAction(IntPtr self, IntPtr selector, IntPtr sender)
    {
        if (!Instances.TryGetValue(self, out var panel) || panel._closed) return;
        try { panel._return(); } catch (Exception error) { panel.SetStatus(error.Message); }
    }
    private static void ExitAction(IntPtr self, IntPtr selector, IntPtr sender)
    { if (Instances.TryGetValue(self, out var panel)) _ = panel.RequestStopAsync(); }
    private static byte KeyEquivalent(IntPtr self, IntPtr selector, IntPtr @event)
    {
        if (!Instances.TryGetValue(self, out var panel) || panel._closed) return 0;
        nuint code = Native.Unsigned(@event, "keyCode"), modifiers = Native.Unsigned(@event, "modifierFlags");
        if (code != 53 && !(code == 13 && (modifiers & (1 << 20)) != 0)) return 0;
        _ = panel.RequestStopAsync(); return 1;
    }
    private static void KeyAction(IntPtr self, IntPtr selector, IntPtr @event)
    {
        if (KeyEquivalent(self, selector, @event) != 0) return;
        var parent = new NativeSuper(self, Native.Class("NSPanel")); Native.SuperAction(ref parent, selector, @event);
    }
    internal static bool IsMiniaturized(Window window) => OperatingSystem.IsMacOS()
        && window.TryGetPlatformHandle() is { HandleDescriptor: "NSWindow" } handle && Native.Boolean(handle.Handle, "isMiniaturized");

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ActionCallback(IntPtr self, IntPtr selector, IntPtr sender);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte EventCallback(IntPtr self, IntPtr selector, IntPtr @event);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte BooleanCallback(IntPtr self, IntPtr selector);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(double X, double Y);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativeRect(double X, double Y, double Width, double Height)
    { internal Size Size => new(Width, Height); }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativeSuper(IntPtr Receiver, IntPtr SuperClass);
    private static class Native
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        [DllImport(ObjC)] private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(ObjC)] internal static extern IntPtr objc_allocateClassPair(IntPtr superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint extraBytes);
        [DllImport(ObjC)] internal static extern void objc_registerClassPair(IntPtr type);
        [DllImport(ObjC)] private static extern byte class_addMethod(IntPtr type, IntPtr selector, IntPtr implementation, [MarshalAs(UnmanagedType.LPUTF8Str)] string encoding);
        [DllImport(ObjC)] private static extern IntPtr object_getClass(IntPtr instance);
        [DllImport(ObjC)] private static extern IntPtr class_getName(IntPtr type);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr instance, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPointer(IntPtr instance, IntPtr selector, IntPtr value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendText(IntPtr instance, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoid(IntPtr instance, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendSet(IntPtr instance, IntPtr selector, IntPtr value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nuint GetUnsigned(IntPtr instance, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte GetBoolean(IntPtr instance, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SetBoolean(IntPtr instance, IntPtr selector, byte value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SetInteger(IntPtr instance, IntPtr selector, nuint value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double GetDouble(IntPtr instance, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte GetPointerBoolean(IntPtr instance, IntPtr selector, IntPtr value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NativeRect GetRectArm(IntPtr instance, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void GetRectIntel(out NativeRect value, IntPtr instance, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern IntPtr InitPanel(IntPtr instance, IntPtr selector, NativeRect rect, nuint style, nuint backing, byte defer);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern IntPtr InitFrame(IntPtr instance, IntPtr selector, NativeRect frame);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void PointSet(IntPtr instance, IntPtr selector, NativePoint point);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern IntPtr HitTest(IntPtr instance, IntPtr selector, NativePoint point);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr ColorMessage(IntPtr instance, IntPtr selector, double red, double green, double blue, double alpha);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr FontMessage(IntPtr instance, IntPtr selector, double size);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern IntPtr KeyEvent(IntPtr instance, IntPtr selector, nuint type, NativePoint point, nuint modifiers,
            double timestamp, nint windowNumber, IntPtr context, IntPtr characters, IntPtr ignoring, byte repeat, ushort keyCode);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern byte EventBoolean(IntPtr instance, IntPtr selector, IntPtr @event);
        [DllImport(ObjC, EntryPoint = "objc_msgSendSuper")] internal static extern void SuperAction(ref NativeSuper parent, IntPtr selector, IntPtr @event);
        internal static IntPtr Class(string name) => objc_getClass(name);
        internal static IntPtr Sel(string name) => sel_registerName(name);
        internal static IntPtr Pointer(IntPtr instance, string selector) => Send(instance, Sel(selector));
        internal static IntPtr Pointer(IntPtr instance, string selector, IntPtr value) => SendPointer(instance, Sel(selector), value);
        internal static void PointerSet(IntPtr instance, string selector, IntPtr value) => SendSet(instance, Sel(selector), value);
        internal static void Void(IntPtr instance, string selector) => SendVoid(instance, Sel(selector));
        internal static bool Boolean(IntPtr instance, string selector) => GetBoolean(instance, Sel(selector)) != 0;
        internal static void Bool(IntPtr instance, string selector, bool value) => SetBoolean(instance, Sel(selector), value ? (byte)1 : (byte)0);
        internal static nuint Unsigned(IntPtr instance, string selector) => GetUnsigned(instance, Sel(selector));
        internal static void Integer(IntPtr instance, string selector, nuint value) => SetInteger(instance, Sel(selector), value);
        internal static double Double(IntPtr instance, string selector) => GetDouble(instance, Sel(selector));
        internal static bool KindOf(IntPtr instance, IntPtr type) => GetPointerBoolean(instance, Sel("isKindOfClass:"), type) != 0;
        internal static bool Responds(IntPtr instance, string selector) => GetPointerBoolean(instance, Sel("respondsToSelector:"), Sel(selector)) != 0;
        internal static IntPtr String(string value) => SendText(Class("NSString"), Sel("stringWithUTF8String:"), value);
        internal static void Text(IntPtr instance, string selector, string value) => PointerSet(instance, selector, String(value));
        internal static string GetText(IntPtr instance, string selector) => Marshal.PtrToStringUTF8(Pointer(Pointer(instance, selector), "UTF8String")) ?? "";
        internal static string ClassName(IntPtr instance) => Marshal.PtrToStringUTF8(class_getName(object_getClass(instance))) ?? "";
        internal static IntPtr Color(double red, double green, double blue) => ColorMessage(Class("NSColor"), Sel("colorWithSRGBRed:green:blue:alpha:"), red, green, blue, 1);
        internal static IntPtr Font(double size) => FontMessage(Class("NSFont"), Sel("systemFontOfSize:"), size);
        internal static NativeRect Rect(IntPtr instance, string selector)
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return GetRectArm(instance, Sel(selector));
            GetRectIntel(out var result, instance, Sel(selector)); return result;
        }
        internal static double PrimaryTop
        {
            get
            {
                var screens = Pointer(Class("NSScreen"), "screens");
                var first = Pointer(screens, "firstObject"); var frame = Rect(first, "frame"); return frame.Y + frame.Height;
            }
        }
        internal static void AddMethod(IntPtr type, string selector, Delegate implementation, string encoding)
        {
            if (class_addMethod(type, Sel(selector), Marshal.GetFunctionPointerForDelegate(implementation), encoding) == 0)
                throw new InvalidOperationException("Не удалось зарегистрировать действие AppKit: " + selector);
        }
    }
}
