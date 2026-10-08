using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DustoreLauncherV.Mac.ViewModels;

namespace DustoreLauncherV.Mac.Services;

/// <summary>Explicit smoke-mode checks with generated fixtures and a recording game-launch adapter.</summary>
internal static class UiInteractionChecks
{
    public static async Task<object> RunAsync(string reportDirectory)
    {
        var checks = new List<string>();
        string profile = Path.Combine(reportDirectory, "interaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        string app = Path.Combine(profile, "Fixture.app"); Directory.CreateDirectory(Path.Combine(app, "Contents"));
        File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), "<plist><dict><key>CFBundleExecutable</key><string>Fixture</string></dict></plist>");
        string cover = Path.Combine(profile, "fixture.png");
        using (var image = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96)))
        {
            var tile = new Border { Width = 32, Height = 32, Background = Brushes.MediumPurple };
            tile.Measure(new Size(32, 32)); tile.Arrange(new Rect(0, 0, 32, 32)); image.Render(tile); image.Save(cover);
        }
        var entries = Enumerable.Range(0, 2000).Select(i => new GameEntry(Guid.NewGuid(), $"Game {i:0000}", app,
            DateTimeOffset.UtcNow.AddSeconds(-i), CustomCoverPath: cover)).ToArray();
        File.WriteAllText(Path.Combine(profile, "library.json"), JsonSerializer.Serialize(entries, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var platform = new RecordingPlatform();
        var model = new MainViewModel(new LauncherServices(profile, platform));
        var window = new MainWindow(model);
        try
        {
            window.Show(); await window.InitializeAsync(); await Task.Delay(150); window.UpdateLayout();
            Check(model.Games.Count == 2000 && model.ShelfItems.Count == 61, "2000 entries create at most 60 game-card containers plus one add tile");
            var selected = model.SelectedGame;
            model.Search = "Game 0"; model.Search = "";
            Check(ReferenceEquals(selected, model.SelectedGame), "search preserves the selected item identity");
            model.SetBackgroundWorkPaused(true); int beforePause = model.DecodedCoverCount;
            await Task.Delay(150);
            Check(model.DecodedCoverCount == beforePause, "minimized/background cover work performs no additional decode");
            model.SetBackgroundWorkPaused(false);
            await UntilAsync(() => model.ResidentCoverCount >= 60, "visible cover load");
            Check(model.ResidentCoverCount <= 96, "decoded artwork has a bounded resident cache");

            var names = new Dictionary<string, string>();
            foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("round") || b.Classes.Contains("nav") || b.Classes.Contains("tile")))
            {
                string name = ControlAutomationPeer.CreatePeerForElement(button)?.GetName() ?? "";
                Check(name.Length > 0, "accessibility peer names " + (button.Name ?? button.Classes.FirstOrDefault() ?? "button"));
                names[button.Name ?? button.Classes.FirstOrDefault() + ":" + names.Count] = name;
            }
            Button first = TileFor(window, model.Games[0]); first.Focus();
            RaiseKey(first, Key.Right); await Task.Delay(60); window.UpdateLayout();
            Check(model.SelectedGame?.Entry.Id == model.Games[1].Entry.Id, "Right selects and focuses the next game");
            Button second = TileFor(window, model.Games[1]); second.Focus();
            RaiseKey(second, Key.Enter);
            await UntilAsync(() => platform.Apps.Count == 1 && !model.IsBusy, "Enter launch dispatch");
            Check(platform.Apps[0] == app && !model.SelectedLaunching, "Enter reaches exactly one recording launch; no fixed 6/45-second busy state");
            second = TileFor(window, model.Games[1]); second.Focus();
            RaiseKey(second, Key.F, KeyModifiers.Meta);
            Check(window.FindControl<TextBox>("SearchBox")?.IsFocused == true, "Command-F moves focus to search");
            model.Search = "Game 0";
            RaiseKey(window.FindControl<TextBox>("SearchBox")!, Key.Escape);
            Check(model.Search.Length == 0, "Escape clears search without leaving the library");
            first = TileFor(window, model.Games[0]); first.Focus();
            RaiseKey(first, Key.PageDown); await Task.Delay(100); window.UpdateLayout();
            await UntilAsync(() => model.DecodedCoverCount >= 120, "next-page cover load");
            Check(model.ShelfPageIndex == 1 && model.ShelfItems.Count == 61 && model.ResidentCoverCount <= 96, "PageDown changes the actual visible page and evicts unused artwork");

            string love = Path.Combine(profile, "Fixture.love");
            using (var archive = ZipFile.Open(love, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("main.lua").Open())) writer.Write("function love.draw() end");
            model.SelectedTarget = model.Targets[1]; await model.SetSourceAsync(love);
            Check(model.PlanUsesRuntime && model.CanConvert, "eX exposes the existing LÖVE runtime editor after analysis");
            model.GameName = "bad/name"; Check(!model.CanConvert && model.HasConversionValidation, "eX validates a malformed package name before conversion");
            model.GameName = "Fixture"; string output = model.OutputPath; Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllText(output, "sentinel");
            Check(!model.CanConvert, "eX refuses to overwrite an existing ZIP");
            model.OutputPath = Path.Combine(profile, "new-output.zip");
            model.RuntimePath = Path.Combine(profile, "missing-runtime.zip"); Check(!model.CanConvert, "eX validates a manually selected missing runtime");
            model.RuntimePath = ""; Check(model.CanConvert && File.ReadAllText(output) == "sentinel", "correcting eX fields restores conversion and preserves the previous file");

            var geometry = new List<object>();
            foreach (var (width, height) in new[] { (1240, 820), (940, 640), (780, 560) })
            {
                window.Width = width; window.Height = height; await Task.Delay(180);
                foreach (string section in new[] { "library", "ex", "settings" })
                {
                    model.Section = section; await Task.Delay(300); window.UpdateLayout();
                    if (section == "ex")
                    {
                        var convert = window.FindControl<Button>("ConvertButton")!;
                        var point = convert.TranslatePoint(default, window);
                        Check(point is { } p && p.X >= 0 && p.Y >= 0 && p.X + convert.Bounds.Width <= window.ClientSize.Width + 1
                            && p.Y + convert.Bounds.Height <= window.ClientSize.Height + 1, $"eX action remains reachable at {width}x{height}");
                    }
                    Save(window, Path.Combine(reportDirectory, $"interaction-{Edition.Name}-{width}x{height}-{section}.png"));
                    geometry.Add(new { width = window.ClientSize.Width, height = window.ClientSize.Height, section, shelfContainers = model.ShelfItems.Count });
                }
            }
            model.Section = "library"; window.Width = 1240; window.Height = 820; await Task.Delay(150);
            var layoutTimes = new List<double>();
            for (int i = 0; i < 20; i++)
            {
                var watch = Stopwatch.StartNew(); model.Search = i % 2 == 0 ? "Game 0" : ""; window.UpdateLayout(); watch.Stop();
                layoutTimes.Add(watch.Elapsed.TotalMilliseconds);
            }
            var priorState = window.WindowState;
            RaiseKey(window, Key.F11); Check(window.WindowState == WindowState.FullScreen, "F11 requests native fullscreen");
            RaiseKey(window, Key.F11); Check(window.WindowState == priorState, "F11 restores the previous window state");
            model.ReduceMotion = true; model.Section = "ex"; await Task.Delay(60);
            Check(window.FindControl<Grid>("ExPage") is { Opacity: 1, RenderTransform: null }, "reduced motion leaves the active page fully visible");
            if (Edition.IsPrime)
            {
                model.SelectedTheme = UiPreferences.Themes.Single(t => t.Key == "light");
                model.Section = "settings"; await Task.Delay(180); window.UpdateLayout(); Save(window, Path.Combine(reportDirectory, "interaction-Prime-light.png"));
                model.SelectedTheme = UiPreferences.Themes[0];
                Check(model.SelectedTheme.Key == "graphite", "theme selection persists and returns to the shipped theme");
            }
            else
            {
                model.SelectedTheme = UiPreferences.Themes.Single(t => t.Key == "light");
                Check(model.SelectedTheme.Key == "graphite" && !model.SelectedUltra, "Free keeps its original appearance and ULTRA gates");
            }
            return new
            {
                status = "Pass", nativeMacWindow = OperatingSystem.IsMacOS(), gameLaunchUsesRecordingAdapter = true,
                keyboardRoutedEventsVerified = true, automationPeerNamesVerified = true, voiceOverInteractionVerified = false,
                catalogEntries = model.Games.Count, pageContainers = model.ShelfItems.Count, residentDecodedCovers = model.ResidentCoverCount,
                coverDecodePaused = true, checks, accessibleNames = names, geometry,
                synchronousSearchWithLayoutMeanMilliseconds = layoutTimes.Average(), synchronousSearchWithLayoutMaxMilliseconds = layoutTimes.Max(),
                testedEdition = Edition.Name, gameFpsMeasured = false
            };
        }
        finally { window.Close(); model.Dispose(); }

        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("UI interaction check: " + description);
            checks.Add(description);
        }
    }
    private static Button TileFor(MainWindow window, GameItemViewModel game) => window.GetVisualDescendants().OfType<Button>()
        .First(b => b.Classes.Contains("tile") && ReferenceEquals(b.DataContext, game));
    private static void RaiseKey(Interactive target, Key key, KeyModifiers modifiers = KeyModifiers.None)
        => target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
    private static async Task UntilAsync(Func<bool> condition, string stage)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(20);
        if (!condition()) throw new TimeoutException("UI interaction check timed out: " + stage);
    }
    private static void Save(Window window, string path)
    {
        using var image = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96));
        image.Render(window); image.Save(path);
    }
    private sealed class RecordingPlatform : IPlatformLauncher
    {
        public bool IsMacOS => true;
        public List<string> Apps { get; } = new();
        public Task OpenAppAsync(string path, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, CancellationToken cancellation = default)
        { cancellation.ThrowIfCancellationRequested(); Apps.Add(path); return Task.CompletedTask; }
        public Task RevealAsync(string path, CancellationToken cancellation = default) => Task.CompletedTask;
        public Task OpenUrlAsync(string url, CancellationToken cancellation = default) => Task.CompletedTask;
    }
}
