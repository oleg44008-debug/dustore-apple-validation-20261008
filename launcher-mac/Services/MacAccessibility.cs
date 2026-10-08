using System.Runtime.InteropServices;

namespace DustoreLauncherV.Mac.Services;

/// <summary>Reads the public AppKit preference on the UI thread, without a background polling timer.</summary>
internal static class MacAccessibility
{
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern IntPtr objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern IntPtr sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern byte SendBool(IntPtr target, IntPtr selector);

    public static bool ReduceMotion()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            IntPtr workspace = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
            return workspace != IntPtr.Zero && SendBool(workspace, sel_registerName("accessibilityDisplayShouldReduceMotion")) != 0;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
}
