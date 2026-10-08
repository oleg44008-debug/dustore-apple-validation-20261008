using System.Diagnostics;
using System.Security;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DustoreLauncherV.Mac.Controls;
using DustoreLauncherV.Mac.ViewModels;

namespace DustoreLauncherV.Mac.Services;

/// <summary>Own fixtures only: real native window/quit checks are separated from engine argument contracts.</summary>
internal static class GameWindowChecks
{
    internal static async Task<object> RunAsync(MainWindow launcher, string reportDirectory)
    {
        var checks = new List<string>();
        var entry = new GameEntry(Guid.NewGuid(), "Проверка игры · 5000×3000", reportDirectory, DateTimeOffset.UtcNow);
        int stops = 0, returns = 0;
        var controls = new GameControlWindow(entry, _ => { stops++; return Task.FromResult(true); }, () => returns++);
        try
        {
            controls.Show(); await Task.Delay(100); controls.UpdateLayout();
            foreach (var metrics in new[] { (780, 560, 1D), (1024, 640, 1D), (1560, 1120, 2D), (2880, 1800, 2D) })
            {
                var area = new PixelRect(24, 40, metrics.Item1, metrics.Item2);
                controls.PlaceInside(area, metrics.Item3); controls.UpdateLayout();
                Check(controls.Position.X >= area.X && controls.Position.Y >= area.Y
                    && controls.Position.X + controls.Width * metrics.Item3 <= area.Right
                    && controls.Position.Y + controls.Height * metrics.Item3 <= area.Bottom,
                    $"Exit panel fits {metrics.Item1}x{metrics.Item2} working pixels at {metrics.Item3:0.#} scaling");
                Check(VisibleButton(controls, controls.ExitButton) && VisibleButton(controls, controls.ReturnButton),
                    "both Exit and Return have actual visible, hittable bounds at " + metrics.Item1 + " pixels");
            }
            Check(ControlAutomationPeer.CreatePeerForElement(controls.ExitButton)?.GetName().Contains(entry.Name, StringComparison.Ordinal) == true,
                "game Exit has a named automation peer identifying its exact game");
            controls.ReturnButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(returns == 1 && stops == 0, "Return does not close the game");
            controls.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await UntilAsync(() => stops == 1, "routed panel Escape");
            Check(!controls.IsVisible, "Escape while the panel has focus invokes game Exit and closes the completed panel");
        }
        finally { controls.Close(); }

        object native = OperatingSystem.IsMacOS() ? await VerifyNativeAsync(launcher, reportDirectory, Check)
            : new { status = "Skipped", reason = "Native AppKit/LaunchServices, active fullscreen Space and owned PID exit require macOS." };
        return new { status = "Pass", checks, native, gameFpsMeasured = false, thirdPartyEngineRuntimeVerified = false,
            globalKeyboardHookInstalled = false, fullscreenKeyboardScope = "Focused controller Escape or Command-W; Command-Tab returns to the launcher." };

        void Check(bool passed, string description)
        { if (!passed) throw new InvalidOperationException("Game window check failed: " + description); checks.Add(description); }
    }

    private static bool VisibleButton(Window window, Button button)
    {
        if (button.TranslatePoint(default, window) is not { } origin || button.Bounds.Width < 60 || button.Bounds.Height < 24) return false;
        var bounds = new Rect(origin, button.Bounds.Size);
        if (!new Rect(window.ClientSize).Contains(bounds)) return false;
        return window.GetVisualAt(bounds.Center) is { } hit && (hit == button || hit.FindAncestorOfType<Button>() == button);
    }

    private static async Task<object> VerifyNativeAsync(MainWindow launcher, string reportDirectory, Action<bool, string> check)
    {
        string root = Path.Combine(reportDirectory, "owned-native-window-" + Guid.NewGuid().ToString("N"));
        string app = Path.Combine(root, "DUSTORE Owned Game.app");
        string contents = Path.Combine(app, "Contents"), resources = Path.Combine(contents, "Resources");
        string executable = Path.Combine(contents, "MacOS", "OwnedGame");
        Directory.CreateDirectory(resources); Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        string fixtureReport = Path.Combine(root, "fixture-native.json"), swift = Path.Combine(root, "OwnedGame.swift");
        await File.WriteAllTextAsync(swift, NativeFixtureSource);
        await File.WriteAllTextAsync(Path.Combine(resources, "report-path.txt"), fixtureReport);
        // This is an explicitly tagged contract fixture, not a shipped Godot runtime or a user game.
        await File.WriteAllTextAsync(Path.Combine(resources, "owned-argument-contract.pck"), "Owned argument contract fixture");
        await File.WriteAllTextAsync(Path.Combine(contents, "Info.plist"), "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict>"
            + "<key>CFBundleExecutable</key><string>OwnedGame</string><key>CFBundleName</key><string>DUSTORE Owned Game</string>"
            + "<key>CFBundleIdentifier</key><string>local.dustore.owned-window." + Guid.NewGuid().ToString("N") + "</string>"
            + "<key>CFBundlePackageType</key><string>APPL</string><key>NSHighResolutionCapable</key><true/></dict></plist>");
        var compile = await WineRuntime.RunAsync("/usr/bin/xcrun", ["swiftc", swift, "-framework", "AppKit", "-o", executable], null, TimeSpan.FromMinutes(2), CancellationToken.None);
        if (compile.Code != 0) throw new IOException("Owned native fixture did not compile: " + compile.Output);
        var sign = await WineRuntime.RunAsync("/usr/bin/codesign", ["--force", "--sign", "-", app], null, TimeSpan.FromSeconds(30), CancellationToken.None);
        if (sign.Code != 0) throw new IOException("Owned native fixture did not sign: " + sign.Output);
        string profile = Path.Combine(root, "launcher-profile");
        var service = new LauncherServices(profile);
        var game = await service.AddGameAsync(app);
        game = await service.SetWindowOptionsAsync(game.Id, GameLaunchOptions.Windowed, 5000, 3000);
        var previousArea = UltraMode.WorkingArea;
        var previousState = launcher.WindowState;
        var observations = new List<object>();
        try
        {
            UltraMode.WorkingArea = (780, 560);
            await service.LaunchAsync(game);
            await UntilAsync(() => NativeAppSessions.Find(game.Id) is not null && launcher.GameControls.Find(game.Id) is not null && File.Exists(fixtureReport), "native owned game and Exit panel");
            var session = NativeAppSessions.Find(game.Id)!; var panel = launcher.GameControls.Find(game.Id)!;
            using var first = JsonDocument.Parse(await File.ReadAllTextAsync(fixtureReport));
            var values = first.RootElement.Clone(); int pid = values.GetProperty("processId").GetInt32();
            double frameW = values.GetProperty("windowFrameWidth").GetDouble(), frameH = values.GetProperty("windowFrameHeight").GetDouble();
            check(session.ProcessId == pid && values.GetProperty("logicalFramebufferWidth").GetInt32() == 5000
                && values.GetProperty("logicalFramebufferHeight").GetInt32() == 3000,
                "LaunchServices tracks the actual own native PID displaying a 5000x3000 logical game surface");
            check(double.IsFinite(session.LaunchStamp) && session.LaunchStamp > 0,
                "native session holds the actual LaunchServices launchDate token in addition to exact bundle and PID");
            check(frameW <= 780 && frameH <= 560 && values.GetProperty("cornersVisible").GetBoolean(),
                "actual native high-resolution fixture fits the compact working-area contract and all four game corners remain visible");
            panel.UpdateLayout();
            check(panel.IsNativePanel && panel.NativeAllSpacesApplied && panel.ExitHittable && panel.ReturnHittable,
                "actual NSPanel auxiliary/all-Spaces policy reads back and native Exit/Return hit testing succeeds");
            check(panel.ExitName.Contains(game.Name, StringComparison.Ordinal) && panel.ReturnName.Contains("лаунчер", StringComparison.Ordinal),
                "actual native game buttons expose localized accessibility labels and identify the exact game");
            check(panel.NativeMovable && CornersUncovered(values, panel.NativeScreenFrame),
                "native title bar is movable and initial placement leaves all four game corner texts uncovered");
            launcher.WindowState = WindowState.Minimized; await Task.Delay(250);
            check(NativeGameControlPanel.IsMiniaturized(launcher) && panel.IsVisible && panel.ExitHittable,
                "actual AppKit launcher miniaturization leaves the unowned NSPanel visible and hittable");
            bool launcherActuallyMiniaturized = NativeGameControlPanel.IsMiniaturized(launcher);
            var launcherWindowsAtMinimize = NativeGameControlPanel.ObserveApplicationWindows();
            long launcherWindowNumber = NativeGameControlPanel.NativeWindowNumberOf(launcher);
            await CaptureDesktopAsync(Path.Combine(reportDirectory, "game-window-native-compact.png"));
            panel.PerformReturn(); await Task.Delay(150);
            check(launcher.WindowState != WindowState.Minimized && NativeAppSessions.Find(game.Id) is not null,
                "actual Return restores the launcher while the exact native game continues running");
            await CapturePanelAsync(panel, Path.Combine(reportDirectory, "game-controls-native.png"));
            await CaptureDesktopAsync(Path.Combine(reportDirectory, "game-window-native-return.png"));
            observations.Add(new { mode = "windowed", fixture = values, panel = PanelMetrics(panel), launcherActuallyMiniaturized,
                launcherWindowNumber, launcherWindowsAtMinimize, nativeLaunchStamp = session.LaunchStamp });
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "game-window-native-snapshots.json"), JsonSerializer.Serialize(new { observations }, new JsonSerializerOptions { WriteIndented = true }));
            panel.SendExitKey(commandW: true);
            await UntilAsync(() => NativeAppSessions.Find(game.Id) is null && !panel.IsVisible, "native game Command-W exit");
            check(!ProcessStillExists(pid), "panel Command-W confirms actual owned native PID exit, not just a quit request");

            File.Delete(fixtureReport);
            UltraMode.WorkingArea = previousArea;
            game = await service.SetWindowOptionsAsync(game.Id, GameLaunchOptions.Fullscreen, 5000, 3000);
            await service.LaunchAsync(game);
            await UntilAsync(() => NativeAppSessions.Find(game.Id) is not null && launcher.GameControls.Find(game.Id) is { IsVisible: true } && File.Exists(fixtureReport), "owned fullscreen reopen");
            await Task.Delay(1500);
            panel = launcher.GameControls.Find(game.Id)!; panel.UpdateLayout();
            using var fullscreen = JsonDocument.Parse(await File.ReadAllTextAsync(fixtureReport));
            var fullValues = fullscreen.RootElement.Clone();
            observations.Add(new { mode = "fullscreen", fixture = fullValues, panel = PanelMetrics(panel), nativeLaunchStamp = NativeAppSessions.Find(game.Id)!.LaunchStamp });
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "game-window-native-snapshots.json"), JsonSerializer.Serialize(new { observations }, new JsonSerializerOptions { WriteIndented = true }));
            await CaptureDesktopAsync(Path.Combine(reportDirectory, "game-window-native-fullscreen.png"));
            check(fullValues.GetProperty("fullscreenRequested").GetBoolean(), "an explicit fullscreen choice remains a real native fullscreen request on reopen");
            check(fullValues.GetProperty("fullscreenActual").GetBoolean() && panel.NativeVisibility.OnActiveSpace
                && panel.NativeVisibility.OcclusionVisible && panel.IsNativePanel && panel.NativeAllSpacesApplied && panel.ExitHittable,
                "actual native fullscreen Space retains an onscreen auxiliary Exit panel with usable control bounds");
            check(CornersUncovered(fullValues, panel.NativeScreenFrame), "fullscreen game corner texts remain uncovered by initial native controller placement");
            panel.PerformExit();
            await UntilAsync(() => NativeAppSessions.Find(game.Id) is null, "native fullscreen game exit");
            check(!ProcessStillExists(fullValues.GetProperty("processId").GetInt32()), "explicit fullscreen own game can actually exit through the persistent game controller");
        }
        finally
        {
            UltraMode.WorkingArea = previousArea;
            if (NativeAppSessions.Find(game.Id) is { } session) await session.StopAsync(true, CancellationToken.None);
            launcher.WindowState = previousState;
        }
        return new { status = "Pass", nativeOwnedFixtureExecuted = true, observations, fixtureSource = swift,
            actualThirdPartyGameExecuted = false, thirdPartyIgnoredWindowArgumentsClamped = false,
            scope = "AppKit native fixture accepting Godot's documented window arguments; actual LaunchServices/PID stop and separate launcher controller." };
    }

    private static object PanelMetrics(GameControlSurface panel)
    {
        return new { panel.Position, panel.ClientSize, panel.RenderScaling, nativeAllSpacesApplied = panel.NativeAllSpacesApplied,
            nativePolicy = panel.NativePolicy,
            nativeOnActiveSpace = panel.NativeVisibility.OnActiveSpace, nativeOcclusionVisible = panel.NativeVisibility.OcclusionVisible,
            screenWorkingArea = panel.ScreenWorkingArea, screenScaling = panel.RenderScaling,
            exitBounds = panel.ExitBounds, returnBounds = panel.ReturnBounds,
            nativeWindowNumber = panel.NativeWindowNumber, nativeIsPanel = panel.IsNativePanel, nativeMovable = panel.NativeMovable,
            nativeScreenFrame = panel.NativeScreenFrame, nativeExitHitTest = panel.ExitHittable, nativeReturnHitTest = panel.ReturnHittable,
            exitAccessibilityLabel = panel.ExitName, returnAccessibilityLabel = panel.ReturnName };
    }
    private static bool CornersUncovered(JsonElement fixture, Rect panelFrame)
    {
        if (!fixture.TryGetProperty("cornerScreenRects", out var corners) || corners.GetArrayLength() != 4) return false;
        return corners.EnumerateArray().All(corner => !panelFrame.Intersects(new Rect(corner.GetProperty("x").GetDouble(), corner.GetProperty("y").GetDouble(),
            corner.GetProperty("width").GetDouble(), corner.GetProperty("height").GetDouble())));
    }
    private static bool ProcessStillExists(int pid)
    { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task CapturePanelAsync(GameControlSurface panel, string path)
    {
        if (panel.IsNativePanel)
        {
            var capture = await WineRuntime.RunAsync("/usr/sbin/screencapture", ["-x", "-l", panel.NativeWindowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), path],
                null, TimeSpan.FromSeconds(15), CancellationToken.None);
            if (capture.Code != 0 || !File.Exists(path)) throw new IOException("Native game controller screenshot failed: " + capture.Output);
        }
        else if (panel.FallbackWindow is { } window)
        {
            using var image = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96));
            image.Render(window); image.Save(path);
        }
    }
    private static async Task CaptureDesktopAsync(string path)
    {
        var capture = await WineRuntime.RunAsync("/usr/sbin/screencapture", ["-x", path], null, TimeSpan.FromSeconds(15), CancellationToken.None);
        if (capture.Code != 0 || !File.Exists(path)) throw new IOException("Owned game desktop screenshot failed: " + capture.Output);
    }
    private static async Task UntilAsync(Func<bool> condition, string description)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        { if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(description); await Task.Delay(50); }
    }

    private const string NativeFixtureSource = """
import AppKit
final class GameSurface: NSView {
    let world = NSSize(width: 5000, height: 3000)
    let text: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 14), .foregroundColor: NSColor.white]
    var fitted: NSRect {
        let scale = min(bounds.width / world.width, bounds.height / world.height)
        return NSRect(x: (bounds.width - world.width * scale) / 2, y: (bounds.height - world.height * scale) / 2,
                      width: world.width * scale, height: world.height * scale)
    }
    var cornerLabels: [(String, NSRect)] {
        let names = ["Левый нижний угол", "Правый нижний угол", "Левый верхний угол", "Правый верхний угол"]
        return names.enumerated().map { index, label in
            let size = (label as NSString).size(withAttributes: text)
            let x = index % 2 == 0 ? fitted.minX + 10 : fitted.maxX - size.width - 10
            let y = index < 2 ? fitted.minY + 10 : fitted.maxY - size.height - 10
            return (label, NSRect(origin: NSPoint(x: x, y: y), size: size))
        }
    }
    var cornersVisible: Bool { cornerLabels.allSatisfy { fitted.contains($0.1) && bounds.contains($0.1) } }
    override func draw(_ dirtyRect: NSRect) {
        NSColor(calibratedRed: 0.08, green: 0.06, blue: 0.13, alpha: 1).setFill()
        bounds.fill()
        NSColor(calibratedRed: 0.73, green: 0.65, blue: 1, alpha: 1).setStroke()
        let outline = NSBezierPath(rect: fitted.insetBy(dx: 2, dy: 2)); outline.lineWidth = 3; outline.stroke()
        for (label, rect) in cornerLabels { (label as NSString).draw(in: rect, withAttributes: text) }
        ("5000 × 3000 · собственный игровой fixture" as NSString).draw(at: NSPoint(x: fitted.midX - 160, y: fitted.midY), withAttributes: text)
    }
}
final class GameDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    var window: NSWindow!
    var report: String = ""
    var full = false
    var launchWidth = 5000.0, launchHeight = 3000.0
    func applicationDidFinishLaunching(_ note: Notification) {
        let args = CommandLine.arguments
        full = args.contains("--fullscreen")
        if let index = args.firstIndex(of: "--resolution"), index + 1 < args.count {
            let size = args[index + 1].split(separator: "x")
            if size.count == 2 { launchWidth = Double(size[0]) ?? 5000; launchHeight = Double(size[1]) ?? 3000 }
        }
        if let file = Bundle.main.url(forResource: "report-path", withExtension: "txt") { report = (try? String(contentsOf: file, encoding: .utf8)) ?? "" }
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: launchWidth, height: launchHeight),
                          styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        window.title = "DUSTORE · собственная игра 5000×3000"
        window.delegate = self
        window.contentView = GameSurface(frame: NSRect(x: 0, y: 0, width: launchWidth, height: launchHeight))
        window.center(); window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true)
        writeReport()
        if full { window.toggleFullScreen(nil) }
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { return true }
    func windowDidResize(_ note: Notification) { writeReport() }
    func windowDidEnterFullScreen(_ note: Notification) { writeReport() }
    func writeReport() {
        guard window != nil, !report.isEmpty, let view = window.contentView, let screen = window.screen ?? NSScreen.main else { return }
        var values: [String: Any] = ["processId": ProcessInfo.processInfo.processIdentifier,
            "logicalFramebufferWidth": 5000, "logicalFramebufferHeight": 3000,
            "requestedWindowWidth": launchWidth, "requestedWindowHeight": launchHeight,
            "clientWidth": view.bounds.width, "clientHeight": view.bounds.height,
            "windowFrameWidth": window.frame.width, "windowFrameHeight": window.frame.height,
            "screenWorkingWidth": screen.visibleFrame.width, "screenWorkingHeight": screen.visibleFrame.height,
            "backingScaleFactor": screen.backingScaleFactor, "fullscreenRequested": full,
            "fullscreenActual": window.styleMask.contains(.fullScreen), "cornersVisible": (view as? GameSurface)?.cornersVisible ?? false]
        if let surface = view as? GameSurface {
            values["cornerScreenRects"] = surface.cornerLabels.map { label, rect in
                let global = window.convertToScreen(surface.convert(rect, to: nil))
                return ["text": label, "x": global.minX, "y": global.minY, "width": global.width, "height": global.height] as [String: Any]
            }
        }
        if #available(macOS 13.0, *) { values["nativeSDKCanJoinAllApplicationsMask"] = NSWindow.CollectionBehavior.canJoinAllApplications.rawValue }
        if let data = try? JSONSerialization.data(withJSONObject: values, options: [.prettyPrinted, .sortedKeys]) {
            try? data.write(to: URL(fileURLWithPath: report), options: .atomic)
        }
    }
}
let app = NSApplication.shared
let delegate = GameDelegate()
app.setActivationPolicy(.regular)
app.delegate = delegate
app.run()
""";
}
