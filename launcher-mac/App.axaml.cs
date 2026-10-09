using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Text.Json;
using DustoreX.AutoConverter;

namespace DustoreLauncherV.Mac;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Dispatcher.UIThread.UnhandledException += (_, args) => StartupDiagnostics.RecordFailure(args.Exception);
        StartupDiagnostics.RecordFrameworkInitialized();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow window;
            try { window = new MainWindow(); }
            catch (Exception error)
            {
                StartupDiagnostics.RecordFailure(error);
                if (Program.UiSmoke) throw;
                desktop.MainWindow = CreateStartupFailureWindow(error);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            desktop.MainWindow = window;
            window.AttachGameLifetime(desktop);
            if (Program.UiSmoke)
            {
                bool uiSmokeStarted = false;
                window.Opened += async (_, _) =>
                {
                    if (uiSmokeStarted) return;
                    uiSmokeStarted = true;
                    try
                    {
                        await window.InitializeAsync();
                        await Task.Delay(2000);
                        if (window.ViewModel.HasError || window.ViewModel.IsBusy)
                            throw new InvalidOperationException("The launcher did not finish loading: " + window.ViewModel.Error);
                        if (!window.IsVisible || window.ClientSize.Width < 800 || window.ClientSize.Height < 500)
                            throw new InvalidOperationException("The native launcher window did not open at a usable size.");
                        window.UpdateLayout();
                        // RenderTargetBitmap's shadow intermediate surfaces use logical coordinates.
                        // Capture at 96 DPI so nested card shadows don't apply desktop scaling twice.
                        double scale = 1;
                        var pixels = new PixelSize((int)Math.Ceiling(window.ClientSize.Width * scale),
                            (int)Math.Ceiling(window.ClientSize.Height * scale));
                        using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
                        bitmap.Render(window);
                        string imagePath = Program.SmokeScreenshotPath;
                        bitmap.Save(imagePath);
                        window.ViewModel.Section = "ex";
                        if (Program.SmokeInputPath is not null)
                        {
                            window.ViewModel.SelectedTarget = window.ViewModel.Targets.Single(t => t.Platform == TargetPlatform.Windows);
                            await window.ViewModel.SetSourceAsync(Program.SmokeInputPath);
                            if (window.ViewModel.HasError || !window.ViewModel.CanConvert)
                                throw new InvalidOperationException("The eX Windows route did not become ready: " + window.ViewModel.Error);
                        }
                        window.UpdateLayout();
                        await Task.Delay(350);
                        window.UpdateLayout();
                        using var exBitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
                        exBitmap.Render(window);
                        string exImagePath = Path.ChangeExtension(Program.SmokeReportPath, ".ex.png");
                        exBitmap.Save(exImagePath);
                        window.ViewModel.Section = "settings";
                        window.UpdateLayout();
                        await Task.Delay(350);
                        window.UpdateLayout();
                        using var settingsBitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
                        settingsBitmap.Render(window);
                        string settingsImagePath = Path.ChangeExtension(Program.SmokeReportPath, ".settings.png");
                        settingsBitmap.Save(settingsImagePath);
                        string emptyProfile = Path.Combine(Path.GetDirectoryName(Program.SmokeReportPath)!, "empty-" + Guid.NewGuid().ToString("N"));
                        using var emptyModel = new ViewModels.MainViewModel(new Services.LauncherServices(emptyProfile));
                        await emptyModel.InitializeAsync();
                        emptyModel.SetPresentationSize(window.ClientSize.Width, window.ClientSize.Height);
                        window.DataContext = emptyModel;
                        await Task.Delay(350);
                        window.UpdateLayout();
                        using var emptyBitmap = new RenderTargetBitmap(pixels, new Vector(96, 96));
                        emptyBitmap.Render(window);
                        string emptyImagePath = Path.ChangeExtension(Program.SmokeReportPath, ".empty.png");
                        emptyBitmap.Save(emptyImagePath);
                        window.DataContext = window.ViewModel;
                        if (Services.Edition.IsPrime)
                        {
                            var intro = Controls.IntroMotion.PlayAsync(window);
                            await Task.Delay(550);
                            using var introBitmap = new RenderTargetBitmap(pixels, new Vector(96, 96));
                            introBitmap.Render(window);
                            introBitmap.Save(Path.ChangeExtension(Program.SmokeReportPath, ".intro.png"));
                            Controls.IntroMotion.Skip();
                            await intro;
                            if (window.FindControl<Grid>("IntroLayer")?.IsVisible == true)
                                throw new InvalidOperationException("Skipping the intro left an overlay over the launcher.");
                        }
                        window.Width = 940; window.Height = 640;
                        foreach (string section in new[] { "library", "ex", "settings" })
                        {
                            window.ViewModel.Section = section;
                            await Task.Delay(350); window.UpdateLayout();
                            using var compactBitmap = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96));
                            compactBitmap.Render(window);
                            compactBitmap.Save(Path.ChangeExtension(Program.SmokeReportPath, ".compact-" + section + ".png"));
                        }
                        window.ViewModel.ReduceMotion = true;
                        window.ViewModel.Section = "ex";
                        await Task.Delay(50);
                        if (window.FindControl<Grid>("ExPage") is not { Opacity: 1, RenderTransform: null })
                            throw new InvalidOperationException("Reduced motion did not keep the new page immediately visible.");
                        window.ViewModel.ReduceMotion = false;
                        window.Width = 1240; window.Height = 820;
                        await Task.Delay(350);
                        var interaction = await Services.UiInteractionChecks.RunAsync(Path.GetDirectoryName(Program.SmokeReportPath)!);
                        // Preserve completed UI evidence even when a later native game or WebKit check fails.
                        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(Program.SmokeReportPath)!, "interaction-report.json"),
                            JsonSerializer.Serialize(interaction, new JsonSerializerOptions { WriteIndented = true }));
                        var gameWindows = await Services.GameWindowChecks.RunAsync(window, Path.GetDirectoryName(Program.SmokeReportPath)!);
                        var web = await VerifyEmbeddedStoreAsync(window);
                        Program.WriteReport(new
                        {
                            status = "Pass", product = "DUSTORE LAUNCHER V", mode = OperatingSystem.IsMacOS() ? "macos-native-desktop-ui-startup" : "avalonia-cross-platform-render",
                            macOSRuntimeVerified = OperatingSystem.IsMacOS(), edition = Services.Edition.Name,
                            operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                            windowOpened = window.IsVisible, clientWidth = window.ClientSize.Width,
                            clientHeight = window.ClientSize.Height, renderScaling = window.RenderScaling,
                            renderedControlsImage = imagePath, interactiveClicksTested = false,
                            exControlsImage = exImagePath, settingsControlsImage = settingsImagePath,
                            emptyControlsImage = emptyImagePath, reducedMotionVerified = true,
                            introSkipVerified = Services.Edition.IsPrime, compactLayoutRendered = true,
                            exAnalysisReady = Program.SmokeInputPath is not null && window.ViewModel.CanConvert,
                            viewModelLoaded = true, libraryEntryCount = window.ViewModel.Games.Count,
                            originalLogoUnchanged = Program.OriginalLogoUnchanged(),
                            embeddedStore = web,
                            iteration2Interaction = interaction,
                            gameWindowVerification = gameWindows,
                            verifiedAtUtc = DateTimeOffset.UtcNow
                        });
                        desktop.Shutdown(0);
                    }
                    catch (Exception error)
                    {
                        Program.WriteReport(new { status = "Fail", mode = "native-desktop-ui-startup", error = error.ToString() });
                        desktop.Shutdown(1);
                    }
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    // Opens the store section and waits for dustore.ru inside the window's own WKWebView.
    private static async Task<object> VerifyEmbeddedStoreAsync(MainWindow window)
    {
        if (Program.OfflineUiSmoke) return new { status = "Skipped", reason = "Offline UI scope: native window, accessibility peers, keyboard, compact layout and catalog checks; remote store is a separate integration check." };
        if (!OperatingSystem.IsMacOS()) return new { status = "Skipped", reason = "WKWebView requires macOS; this run verifies only Avalonia rendering and launcher services." };
        window.ViewModel.Section = "store";
        if (window.WebView is not { } web)
            throw new InvalidOperationException("The in-app store web view was not created on macOS.");
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(45))
        {
            await Task.Delay(500);
            var state = web.State;
            if (web.IsCreated && state.Url.StartsWith("https://", StringComparison.Ordinal) && !state.IsLoading && state.Title.Length > 0) break;
        }
        if (!web.IsCreated)
            throw new InvalidOperationException("WKWebView was not created inside the launcher window.");
        var final = web.State;
        if (!Uri.TryCreate(final.Url, UriKind.Absolute, out var url) || !url.Host.EndsWith("dustore.ru", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The in-app store did not navigate to dustore.ru: '" + final.Url + "'.");
        // A desktop capture shows the native web view, which RenderTargetBitmap cannot draw.
        string capture = Path.ChangeExtension(Program.SmokeReportPath, ".store.png");
        try
        {
            using var screen = System.Diagnostics.Process.Start("/usr/sbin/screencapture", new[] { "-x", capture });
            screen?.WaitForExit(15000);
        }
        catch (Exception) { capture = ""; }

        // A load that cannot connect must be explained on screen, not left as an empty page.
        // Port 59999 is unused and, unlike ports such as 9, not on WebKit's restricted list.
        web.Navigate("https://127.0.0.1:59999/");
        var failureStarted = DateTime.UtcNow;
        while (!window.ViewModel.HasWebError && DateTime.UtcNow - failureStarted < TimeSpan.FromSeconds(20))
            await Task.Delay(250);
        if (!window.ViewModel.HasWebError || window.ViewModel.WebViewVisible)
            throw new InvalidOperationException("A failed page load did not show the launcher's error screen. "
                + $"started={Services.WebKitBridge.StartedCount} failed={Services.WebKitBridge.FailedCount} raw='{Services.WebKitBridge.LastRawFailure}' "
                + $"url='{web.State.Url}' loading={web.State.IsLoading} bridgeError={(Services.WebKitBridge.LastError is null ? "none" : "set")}");
        var failure = window.ViewModel.WebError!;
        window.ViewModel.ClearWebError();
        object? download = Program.SmokeInputPath is { } game && game.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? await VerifyStoreDownloadAsync(window, web, game) : null;
        return new
        {
            storeDownload = download,
            failedLoadShowsError = true, failedLoadCode = failure.Code, failedLoadDomain = failure.Domain,
            navigationCallbacks = new { started = Services.WebKitBridge.StartedCount, failed = Services.WebKitBridge.FailedCount },
            webViewCreated = true, url = final.Url, title = final.Title, finishedLoading = !final.IsLoading,
            secondsToLoad = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1),
            screenCapture = File.Exists(capture) ? capture : null, insideLauncherWindow = true
        };
    }

    // Mirrors the store: a game page whose download link answers 302 to an S3-style
    // application/zip without Content-Disposition. The game must land in the library.
    // 127.0.0.1:<port> stands in for dustore.ru (trusted store); another port is any other site.
    private static async Task<object> VerifyStoreDownloadAsync(MainWindow window, Controls.NativeWebView web, string gameZip)
    {
        // Two loopback ports act as two sites; only the store's exact address is trusted.
        int port = StartAndStop(System.Net.Sockets.TcpListener.Create(0));
        int otherPort = StartAndStop(System.Net.Sockets.TcpListener.Create(0));
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add($"http://127.0.0.1:{otherPort}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch { return; }
                try
                {
                    string path = context.Request.Url?.AbsolutePath ?? "/";
                    if (path == "/g/1")
                    {
                        byte[] html = System.Text.Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset=\"utf-8\"><title>Dustore — PODIEZD</title>"
                            + "<meta http-equiv=\"refresh\" content=\"1;url=/download_game.php?game_id=1\"></head><body>Скачать</body></html>");
                        context.Response.ContentType = "text/html; charset=utf-8";
                        await context.Response.OutputStream.WriteAsync(html);
                    }
                    else if (path == "/download_game.php")
                    {
                        context.Response.StatusCode = 302;
                        context.Response.RedirectLocation = "/builds/game-1/build-8df380fa84ff.zip";
                    }
                    else if (path.EndsWith(".zip", StringComparison.Ordinal))
                    {
                        context.Response.ContentType = "application/zip";
                        await using var file = File.OpenRead(gameZip);
                        context.Response.ContentLength64 = file.Length;
                        await file.CopyToAsync(context.Response.OutputStream);
                    }
                    else context.Response.StatusCode = 404;
                }
                catch { }
                finally { try { context.Response.Close(); } catch { } }
            }
        });

        ViewModels.MainViewModel.TrustedStoreHosts.Add("127.0.0.1:" + port);
        int before = window.ViewModel.Games.Count;
        var store = await DownloadThroughPageAsync(window, web, $"http://127.0.0.1:{port}/g/1");
        var other = await DownloadThroughPageAsync(window, web, $"http://127.0.0.1:{otherPort}/g/1");
        listener.Stop();
        if (store.downloadQuarantined || store.preparedAppQuarantined)
            throw new InvalidOperationException("A store download kept the quarantine marker, so the game would stop at Gatekeeper.");
        if (!other.downloadQuarantined)
            throw new InvalidOperationException("A download from another site lost its quarantine marker.");
        object overlap = await VerifyOverlappingDownloadsAsync(window, web, gameZip);
        return new
        {
            addedToLibrary = true, readyToLaunch = true, libraryBefore = before, libraryAfter = window.ViewModel.Games.Count,
            name = store.name, file = store.file, bytes = store.bytes, pageAfterDownload = store.page,
            downloadQuarantined = store.downloadQuarantined, preparedAppQuarantined = store.preparedAppQuarantined,
            otherSiteDownloadQuarantined = other.downloadQuarantined, otherSitePreparedAppQuarantined = other.preparedAppQuarantined,
            overlappingDownloads = overlap
        };
    }

    private static async Task<object> VerifyOverlappingDownloadsAsync(MainWindow window, Controls.NativeWebView web, string gameZip)
    {
        int port = StartAndStop(System.Net.Sockets.TcpListener.Create(0));
        string origin = $"http://127.0.0.1:{port}";
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add(origin + "/"); listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch { return; }
                // A stalled response must not prevent the second real WebKit request.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string path = context.Request.Url?.AbsolutePath ?? "/";
                        bool first = path.Contains("first", StringComparison.Ordinal);
                        if (path.EndsWith(".zip", StringComparison.Ordinal))
                        {
                            context.Response.ContentType = "application/zip";
                            await using var file = File.OpenRead(gameZip);
                            context.Response.ContentLength64 = file.Length;
                            byte[] head = new byte[(int)Math.Max(1, Math.Min(4096, file.Length / 2))];
                            await file.ReadExactlyAsync(head);
                            await context.Response.OutputStream.WriteAsync(head);
                            await context.Response.OutputStream.FlushAsync();
                            await (first ? releaseFirst.Task : releaseSecond.Task).WaitAsync(TimeSpan.FromSeconds(30));
                            await file.CopyToAsync(context.Response.OutputStream);
                        }
                        else
                        {
                            string name = first ? "first" : "second";
                            byte[] html = System.Text.Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset=\"utf-8\"><title>Dustore — overlap " + name
                                + "</title><meta http-equiv=\"refresh\" content=\"1;url=/" + name + ".zip\"></head><body>Скачать</body></html>");
                            context.Response.ContentType = "text/html; charset=utf-8";
                            await context.Response.OutputStream.WriteAsync(html);
                        }
                    }
                    catch { }
                    finally { try { context.Response.Close(); } catch { } }
                });
            }
        });
        string trustedHost = "127.0.0.1:" + port;
        ViewModels.MainViewModel.TrustedStoreHosts.Add(trustedHost);
        int ignoredBefore = Services.WebKitBridge.SupersededDownloadCallbackCount;
        int libraryBefore = window.ViewModel.Games.Count;
        try
        {
            int priorId = Services.WebKitBridge.CurrentDownload?.Id ?? 0;
            web.Navigate(origin + "/first");
            await UntilAsync(() => Services.WebKitBridge.CurrentDownload is { Status: Services.DownloadStatus.Running, Name: "overlap first" } first
                && first.Id > priorId && first.Path.Length > 0, "the first held native download");
            var first = Services.WebKitBridge.CurrentDownload!;
            web.Navigate(origin + "/second");
            await UntilAsync(() => Services.WebKitBridge.CurrentDownload is { Status: Services.DownloadStatus.Running, Name: "overlap second" } second
                && second.Id > first.Id && second.Path.Length > 0, "the replacement held native download");
            var second = Services.WebKitBridge.CurrentDownload!;
            releaseFirst.TrySetResult();
            await UntilAsync(() => Services.WebKitBridge.SupersededDownloadCallbackCount > ignoredBefore,
                "an actual terminal callback from the superseded WKDownload");
            await Task.Delay(500); // Let the ordinary UI download poll observe any corrupted status.
            if (Services.WebKitBridge.CurrentDownload is not { Status: Services.DownloadStatus.Running } running
                || running.Id != second.Id || running.Name != second.Name || running.Path != second.Path
                || window.ViewModel.Games.Count != libraryBefore || window.ViewModel.DownloadedGameId is not null)
                throw new InvalidOperationException("A superseded WKDownload changed or imported the still-incomplete replacement.");
            releaseSecond.TrySetResult();
            await UntilAsync(() => window.ViewModel.LastDownload?.Id == second.Id && window.ViewModel.DownloadReady,
                "the complete replacement's ordinary library import", TimeSpan.FromSeconds(90));
            await using var expectedFile = File.OpenRead(gameZip);
            await using var actualFile = File.OpenRead(second.Path);
            if (!(await System.Security.Cryptography.SHA256.HashDataAsync(expectedFile)).SequenceEqual(
                    await System.Security.Cryptography.SHA256.HashDataAsync(actualFile))
                || window.ViewModel.Games.Count != libraryBefore + 1)
                throw new InvalidOperationException("The replacement download was imported before its complete original bytes arrived.");
            return new { status = "Pass", supersededDownloadCallbacks = Services.WebKitBridge.SupersededDownloadCallbackCount - ignoredBefore,
                incompleteReplacementImported = false, completeReplacementImported = true, exactFileBytesPreserved = true };
        }
        finally
        {
            releaseFirst.TrySetResult(); releaseSecond.TrySetResult(); listener.Stop();
            ViewModels.MainViewModel.TrustedStoreHosts.Remove(trustedHost);
        }

        static async Task UntilAsync(Func<bool> predicate, string description, TimeSpan? timeout = null)
        {
            var started = DateTime.UtcNow;
            while (!predicate() && DateTime.UtcNow - started < (timeout ?? TimeSpan.FromSeconds(15))) await Task.Delay(100);
            if (!predicate()) throw new TimeoutException("WebKit overlap check timed out: " + description);
        }
    }

    private static async Task<(string name, string file, long bytes, string page, bool downloadQuarantined, bool preparedAppQuarantined)>
        DownloadThroughPageAsync(MainWindow window, Controls.NativeWebView web, string pageUrl)
    {
        int previous = window.ViewModel.LastDownload?.Id ?? 0;
        web.Navigate(pageUrl);
        var started = DateTime.UtcNow;
        while (!(window.ViewModel.LastDownload?.Id > previous && window.ViewModel.DownloadedGameId is not null)
               && DateTime.UtcNow - started < TimeSpan.FromSeconds(90))
        {
            await Task.Delay(500);
            if (window.ViewModel.LastDownload is { Status: Services.DownloadStatus.Failed or Services.DownloadStatus.Cancelled } broken && broken.Id > previous)
                throw new InvalidOperationException("The store download failed: " + broken.Error);
            if (window.ViewModel.HasWebError)
                throw new InvalidOperationException($"The download page {pageUrl} failed: {window.ViewModel.WebErrorMessage} ({window.ViewModel.WebError?.FailingUrl})");
        }
        var downloaded = window.ViewModel.LastDownload;
        if (window.ViewModel.DownloadedGameId is not { } id || downloaded is null || downloaded.Id <= previous)
            throw new InvalidOperationException($"The download from {pageUrl} did not reach the library (state {downloaded?.Status}, page '{web.State.Url}').");
        var entry = window.ViewModel.Games.FirstOrDefault(g => g.Entry.Id == id)?.Entry;
        if (entry is null || !entry.CanLaunchOnMac)
            throw new InvalidOperationException("The downloaded game was not prepared for launch on Mac.");
        if (web.State.Url.EndsWith(".zip", StringComparison.Ordinal))
            throw new InvalidOperationException("The view navigated to the file instead of keeping the store page.");
        return (downloaded.Name, Path.GetFileName(downloaded.Path), new FileInfo(downloaded.Path).Length, web.State.Url,
            Services.MacQuarantine.Read(downloaded.Path) is not null,
            entry.PreparedMacAppPath is { } app && Services.MacQuarantine.Read(app) is not null);
    }

    private static int StartAndStop(System.Net.Sockets.TcpListener probe)
    {
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static Window CreateStartupFailureWindow(Exception error)
    {
        var window = new Window
        {
            Title = "Ошибка запуска DUSTORE LAUNCHER V", Width = 640, Height = 390,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        panel.Children.Add(new TextBlock
        {
            Text = "Не удалось загрузить лаунчер. Подробности сохранены в журнале.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 18
        });
        panel.Children.Add(new TextBox
        {
            Text = error.Message + "\n\nЖурнал: " + StartupDiagnostics.LogPath,
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Height = 180
        });
        var close = new Button { Content = "Закрыть", Padding = new Thickness(16, 8) };
        close.Click += (_, _) => window.Close();
        panel.Children.Add(close);
        window.Content = panel;
        return window;
    }
}
