using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Threading;

namespace DustoreLauncherV.Mac.Services;

/// <summary>Observes exact launcher-opened bundles through AppKit, without scanning unrelated processes.</summary>
internal static class NativeAppSessions
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Guid, NativeSession> Active = new();
    private static DispatcherTimer? _monitor;
    internal static event EventHandler<GameSessionEventArgs>? Changed;
    internal static NativeSession? Find(Guid id) { lock (Gate) return Active.GetValueOrDefault(id); }
    internal static NativeSession[] Snapshot() { lock (Gate) return Active.Values.ToArray(); }

    internal static async Task<bool> ObserveAsync(GameEntry entry, string bundle, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsMacOS() || Application.Current is null) return false;
        bundle = Path.GetFullPath(bundle).TrimEnd(Path.DirectorySeparatorChar);
        // LaunchServices may return just before its application appears in NSWorkspace.
        for (int attempt = 0; attempt < 16; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            int pid = await Dispatcher.UIThread.InvokeAsync(() => AppKit.FindBundle(bundle));
            if (pid > 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var session = new NativeSession(entry, bundle, pid);
                    lock (Gate) Active[entry.Id] = session;
                    _monitor ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Poll());
                    _monitor.Start();
                    Changed?.Invoke(null, new(entry.Id, "native app"));
                });
                return true;
            }
            await Task.Delay(125, cancellation).ConfigureAwait(false);
        }
        return false;
    }

    private static void Poll()
    {
        foreach (var session in Snapshot())
        {
            if (AppKit.StillRunning(session.ProcessId, session.Bundle)) continue;
            lock (Gate) Active.Remove(session.Entry.Id);
            Changed?.Invoke(null, new(session.Entry.Id, "native app", 0));
        }
        lock (Gate) if (Active.Count == 0) _monitor?.Stop();
    }

    internal sealed class NativeSession(GameEntry entry, string bundle, int processId)
    {
        internal GameEntry Entry { get; } = entry;
        internal string Bundle { get; } = bundle;
        internal int ProcessId { get; } = processId;
        internal async Task<bool> StopAsync(bool force, CancellationToken cancellation)
        {
            bool requested = await Dispatcher.UIThread.InvokeAsync(() => AppKit.RequestQuit(ProcessId, Bundle, force));
            if (!requested && await Dispatcher.UIThread.InvokeAsync(() => AppKit.StillRunning(ProcessId, Bundle)))
                throw new IOException("Игра не приняла запрос закрытия. Можно закрыть её принудительно.");
            for (int attempt = 0; attempt < 20; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!await Dispatcher.UIThread.InvokeAsync(() => AppKit.StillRunning(ProcessId, Bundle)))
                {
                    await Dispatcher.UIThread.InvokeAsync(Poll);
                    return true;
                }
                await Task.Delay(100, cancellation).ConfigureAwait(false);
            }
            // terminate() confirms the request, not completion. Keep the panel until actual exit.
            return false;
        }
    }

    private static class AppKit
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        [DllImport(ObjC)] private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Pointer(IntPtr instance, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr AtIndex(IntPtr instance, IntPtr selector, nuint index);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr WithPid(IntPtr instance, IntPtr selector, int pid);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nuint Count(IntPtr instance, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern int Integer(IntPtr instance, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")][return: MarshalAs(UnmanagedType.I1)]
        private static extern bool Boolean(IntPtr instance, IntPtr selector);
        private static IntPtr S(string name) => sel_registerName(name);
        private static string PathOf(IntPtr application)
        {
            IntPtr url = Pointer(application, S("bundleURL"));
            IntPtr path = url == IntPtr.Zero ? IntPtr.Zero : Pointer(url, S("path"));
            return path == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(Pointer(path, S("UTF8String"))) ?? "";
        }
        private static IntPtr ExactApplication(int pid, string bundle)
        {
            IntPtr app = WithPid(objc_getClass("NSRunningApplication"), S("runningApplicationWithProcessIdentifier:"), pid);
            return app != IntPtr.Zero && !Boolean(app, S("isTerminated")) && PathOf(app).Equals(bundle, StringComparison.Ordinal)
                ? app : IntPtr.Zero;
        }
        internal static int FindBundle(string bundle)
        {
            Dispatcher.UIThread.VerifyAccess();
            IntPtr workspace = Pointer(objc_getClass("NSWorkspace"), S("sharedWorkspace"));
            IntPtr apps = Pointer(workspace, S("runningApplications"));
            for (nuint index = 0, count = Count(apps, S("count")); index < count; index++)
            {
                IntPtr app = AtIndex(apps, S("objectAtIndex:"), index);
                if (PathOf(app).Equals(bundle, StringComparison.Ordinal) && !Boolean(app, S("isTerminated")))
                    return Integer(app, S("processIdentifier"));
            }
            return 0;
        }
        internal static bool StillRunning(int pid, string bundle)
        {
            Dispatcher.UIThread.VerifyAccess();
            return ExactApplication(pid, bundle) != IntPtr.Zero;
        }
        internal static bool RequestQuit(int pid, string bundle, bool force)
        {
            Dispatcher.UIThread.VerifyAccess();
            IntPtr app = ExactApplication(pid, bundle);
            return app == IntPtr.Zero || Boolean(app, S(force ? "forceTerminate" : "terminate"));
        }
    }
}
