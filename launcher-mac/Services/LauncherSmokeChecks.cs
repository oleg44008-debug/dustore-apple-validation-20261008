using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DustoreX.AutoConverter;

namespace DustoreLauncherV.Mac.Services;

public sealed record LauncherSmokeReport(bool Success, IReadOnlyList<string> Checks, string ProfileDirectory,
    string? InputPath, bool SourcePreserved, string? Error = null, string? PreparedMacAppPath = null, object? SessionVerification = null);

public static class LauncherSmokeChecks
{
    public static async Task<LauncherSmokeReport> RunAsync(string? inputPath = null, string? profileDirectory = null,
        CancellationToken cancellation = default)
    {
        var checks = new List<string>();
        profileDirectory ??= Environment.GetEnvironmentVariable("DUSTOREV_PROFILE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(profileDirectory))
            throw new ArgumentException("Smoke checks require an explicitly isolated profile directory.", nameof(profileDirectory));
        profileDirectory = Path.GetFullPath(profileDirectory);
        bool sourcePreserved = false;
        string? prepared = null;
        object? sessionVerification = null;
        try
        {
            var fakePlatform = new RecordingPlatform();
            var service = new LauncherServices(profileDirectory, fakePlatform);
            string fixtures = Path.Combine(profileDirectory, "smoke-fixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtures);
            string emptyProfile = Path.Combine(fixtures, "empty-startup-profile");
            var emptyService = new LauncherServices(emptyProfile, fakePlatform);
            VerifyWindowArguments(Check);
            await VerifyCaptureConfigurationAsync(fixtures, cancellation, Check).ConfigureAwait(false);
            Check(GameLaunchOptions.WineGodotRenderer.Contains("opengl3") && GameLaunchOptions.WineGodotRenderer.Contains("gl_compatibility"),
                "Godot under Wine starts on the OpenGL Compatibility renderer instead of Vulkan");
            Check(!GameLaunchOptions.Environment(true, "/tmp").ContainsKey("DXVK_ASYNC") && GameLaunchOptions.Environment(true, "/tmp")["DXVK_LOG_PATH"] == "/tmp",
                "Wine games log DXVK and no longer compile shaders asynchronously by default");
            Check(GameLaunchOptions.Arguments(GameEngineKind.Godot, GameLaunchOptions.Fullscreen, 1600, 900).SequenceEqual(new[] { "--fullscreen", "--resolution", "1600x900" }) && GameLaunchOptions.Arguments(GameEngineKind.Unity, GameLaunchOptions.GameDefault, 1600, 900).Count == 0, "Godot and game-default window options map to the right command line");
            if (!Edition.IsPrime)
            {
                string quotaDir = Path.Combine(Path.GetTempPath(), "dustore-quota-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(quotaDir);
                var serverNow = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
                TrustedClock.ServerOverride = () => serverNow;
                await TrustedClock.SyncAsync(quotaDir).ConfigureAwait(false);
                for (int i = 0; i < ExDailyQuota.FreePerDay; i++) ExDailyQuota.RecordSuccess(quotaDir);
                Check(ExDailyQuota.Refusal(quotaDir) is not null, "Free refuses the fourth eX transfer of a server day");
                serverNow = serverNow.AddHours(-30);
                await TrustedClock.SyncAsync(quotaDir).ConfigureAwait(false);
                Check(ExDailyQuota.Refusal(quotaDir) is not null, "winding time back does not reopen the eX quota");
                ExDailyQuota.DeleteFirstCopy(quotaDir);
                Check(ExDailyQuota.Refusal(quotaDir) is not null, "deleting the profile copy keeps the eX quota");
                ExDailyQuota.CorruptFirstCopy(quotaDir);
                Check(ExDailyQuota.Refusal(quotaDir) is not null, "an edited quota file reads as used up");
                ExDailyQuota.Clear(quotaDir);
                serverNow = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
                await TrustedClock.SyncAsync(quotaDir).ConfigureAwait(false);
                for (int i = 0; i < ExDailyQuota.FreePerDay; i++) ExDailyQuota.RecordSuccess(quotaDir);
                serverNow = new DateTimeOffset(2026, 10, 4, 21, 1, 0, TimeSpan.Zero);
                await TrustedClock.SyncAsync(quotaDir).ConfigureAwait(false);
                Check(ExDailyQuota.UsedToday(quotaDir) == 0, "a new Moscow day by server time reopens the eX quota");
                ExDailyQuota.Clear(quotaDir);
                TrustedClock.ServerOverride = null;
                Directory.Delete(quotaDir, true);
            }
            Check(UltraMode.Arguments(GameEngineKind.Unity).Count == 0 && UltraMode.Arguments(GameEngineKind.Godot).Count == 0,
                "ULTRA keeps the game's resolution, quality and frame pacing");
            string ultraDir = Path.Combine(Path.GetTempPath(), "dustore-ultra-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ultraDir);
            var ultraEnv = new Dictionary<string, string>();
            UltraMode.AddDxvk(ultraEnv, ultraDir);
            string dxvkConf = File.ReadAllText(ultraEnv["DXVK_CONFIG_FILE"]);
            Check(!dxvkConf.Contains("syncInterval") && !dxvkConf.Contains("samplerAnisotropy") && !dxvkConf.Contains("relaxedBarriers")
                && ultraEnv["DXVK_STATE_CACHE"] == "1" && Directory.Exists(ultraEnv["DXVK_STATE_CACHE_PATH"]) && ultraEnv["WINEDEBUG"] == "-all",
                "ULTRA DXVK route is uncapped with a persistent shader cache and no image-quality cuts");
            var metalEnv = new Dictionary<string, string>(); var metalConfig = new List<string>();
            UltraMode.AddMetal(metalEnv, metalConfig);
            Check(metalConfig.Count == 0 && !metalEnv.ContainsKey("DXMT_METALFX_SPATIAL_SWAPCHAIN") && !metalConfig.Any(c => c.Contains("Upscale")),
                "ULTRA Metal route renders at native resolution without upscaling");
            Check(ultraEnv["DXVK_FRAME_RATE"] == "0" && ultraEnv["WINEESYNC"] == "0" && ultraEnv["WINEMSYNC"] is "0" or "1",
                "ULTRA removes only the launcher frame cap and never enables competing Wine synchronization backends");
            var child = new System.Diagnostics.ProcessStartInfo("wine64");
            child.Environment["DXVK_CONFIG_FILE"] = "inherited-other-renderer.conf";
            child.Environment["DXMT_CONFIG"] = "inherited-limit=30";
            child.Environment["D3DM_SUPPORT_ENVIRONMENT"] = "1";
            string? originalDxvk = Environment.GetEnvironmentVariable("DXVK_CONFIG_FILE");
            UltraMode.ConfigureGameProcess(child, false, new Dictionary<string, string> { ["DXMT_SHADER_CACHE"] = "1" });
            Check(!child.Environment.ContainsKey("DXVK_CONFIG_FILE") && !child.Environment.ContainsKey("DXMT_CONFIG")
                && !child.Environment.ContainsKey("D3DM_SUPPORT_ENVIRONMENT") && child.Environment["DXMT_SHADER_CACHE"] == "1"
                && Environment.GetEnvironmentVariable("DXVK_CONFIG_FILE") == originalDxvk,
                "graphics profile cleanup is child-only and preserves the launcher's environment");
            Check(Edition.IsPrime == Edition.IsPrimeBuild,
                "Free and Prime are separate compile-time editions with no runtime activation dependency");
            Directory.Delete(ultraDir, true);
            sessionVerification = await SessionChecks.RunAsync(fixtures, cancellation).ConfigureAwait(false);
            Check(!Directory.Exists(emptyProfile), "service construction performs no profile filesystem writes before the GUI");
            Check((await emptyService.LoadLibraryAsync(cancellation).ConfigureAwait(false)).Count == 0
                && Directory.Exists(emptyProfile), "an empty profile is created and loaded during async initialization");
            string blockedProfile = Path.Combine(fixtures, "blocked-profile-file");
            File.WriteAllText(blockedProfile, "Existing file must stay unchanged.");
            string blockedHash = HashSource(blockedProfile);
            var blockedService = new LauncherServices(blockedProfile, fakePlatform);
            await MustThrowAsync<IOException>(() => blockedService.LoadLibraryAsync(cancellation));
            Check(HashSource(blockedProfile) == blockedHash, "profile file collisions become async initialization errors without changing the file");
            var blockedParentService = new LauncherServices(Path.Combine(blockedProfile, "child"), fakePlatform);
            await MustThrowAsync<IOException>(() => blockedParentService.LoadLibraryAsync(cancellation));
            Check(HashSource(blockedProfile) == blockedHash, "an unwritable profile under a file parent is rejected after construction without changing data");
            var blankProfileService = new LauncherServices("", fakePlatform);
            await MustThrowAsync<InvalidDataException>(() => blankProfileService.LoadLibraryAsync(cancellation));
            Check(blankProfileService.DataDirectory == "" && blankProfileService.OutputDirectory == "", "a blank profile override surfaces an async error without falling back to another profile");
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                string restrictedParent = Path.Combine(fixtures, "permission-restricted-profile");
                Directory.CreateDirectory(restrictedParent);
                UnixFileMode prior = File.GetUnixFileMode(restrictedParent);
                try
                {
                    File.SetUnixFileMode(restrictedParent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    var restrictedService = new LauncherServices(Path.Combine(restrictedParent, "child"), fakePlatform);
                    await MustThrowAsync<IOException>(() => restrictedService.LoadLibraryAsync(cancellation));
                    Check(!Directory.Exists(Path.Combine(restrictedParent, "child")), "a non-writable profile parent produces a recoverable async error before any game data is changed");
                }
                finally { File.SetUnixFileMode(restrictedParent, prior); }
            }
            string portable = Path.Combine(fixtures, "Portable.love");
            WriteZip(portable, ("main.lua", Encoding.UTF8.GetBytes("function love.draw() love.graphics.print('DUSTORE', 20, 20) end"), 0));
            string selected = inputPath is null ? portable : Path.GetFullPath(inputPath);
            string before = HashSource(selected);

            var entry = await service.AddGameAsync(selected, cancellation).ConfigureAwait(false);
            Check(entry.SourcePath == selected, "library preserves the original source path");
            var duplicate = await service.AddGameAsync(selected, cancellation).ConfigureAwait(false);
            Check(duplicate.Id == entry.Id, "adding the same source preserves its library ID");
            var reloaded = await new LauncherServices(profileDirectory, fakePlatform).LoadLibraryAsync(cancellation).ConfigureAwait(false);
            Check(reloaded.Any(e => e.Id == entry.Id && e.SourcePath == selected), "library IDs and source paths survive reload");
            Check(File.Exists(service.LibraryPath), "library is saved in the isolated profile");

            var plan = await service.InspectAsync(selected, TargetPlatform.Windows, "x64", cancellation).ConfigureAwait(false);
            Check(plan.InputPath == selected, "pure conversion core analyzes the selected input");
            Check(!string.IsNullOrWhiteSpace(plan.Engine) && plan.Warnings is not null, "conversion analysis exposes engine and compatibility warnings");
            if (inputPath is not null) Check(plan.CanConvert && plan.Method == "godot", "PODIEZD Mac package supports the real reverse Windows route");
            prepared = entry.PreparedMacAppPath;
            if (inputPath is not null)
                Check(prepared is not null && Directory.Exists(prepared) && File.Exists(Path.Combine(prepared, "Contents", "Info.plist")), "Mac package is imported into a separate owned app bundle");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await MustThrowAsync<OperationCanceledException>(() => service.InspectAsync(portable, TargetPlatform.MacOS, "arm64", cancelled.Token));
            checks.Add("analysis respects cancellation before work starts");
            await MustThrowAsync<OperationCanceledException>(() => service.AddGameAsync(portable, cancelled.Token));
            checks.Add("cancelled library mutations do not write data");
            string output = service.NewOutputPath("../A:B\\C", TargetPlatform.MacOS);
            Check(Path.GetDirectoryName(output) == service.OutputDirectory && Path.GetExtension(output) == ".zip", "default output is a separate ZIP within the profile");
            Check(service.NewOutputPath("Game", TargetPlatform.Windows) != service.NewOutputPath("Game", TargetPlatform.Windows), "output names avoid collisions");

            string appZip = Path.Combine(fixtures, "Tiny-macOS.zip");
            WriteZip(appZip,
                ("Tiny.app/Contents/Info.plist", Encoding.UTF8.GetBytes("<?xml version='1.0'?><plist version='1.0'><dict><key>CFBundleExecutable</key><string>Tiny</string></dict></plist>"), 0x81A4),
                ("Tiny.app/Contents/MacOS/Tiny", new byte[] { 0xCF, 0xFA, 0xED, 0xFE, 7, 0, 0, 1 }, 0x81ED));
            byte[] quarantine = Encoding.UTF8.GetBytes("0083;00000001;DUSTORE-smoke;");
            if (OperatingSystem.IsMacOS()) MacQuarantine.Write(appZip, quarantine);
            string archiveHash = HashSource(appZip);
            var imported = await service.AddGameAsync(appZip, cancellation).ConfigureAwait(false);
            Check(imported.CanLaunchOnMac && imported.PreparedMacAppPath is not null, "Mac ZIP import produces a launchable library shortcut");
            Check(HashSource(appZip) == archiveHash, "ZIP import preserves the source archive bytes");
            Check(imported.PreparedMacAppPath!.StartsWith(service.ManagedGamesDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal), "prepared apps are stored only in the managed games directory");
            if (OperatingSystem.IsMacOS())
                Check(MacQuarantine.Read(imported.PreparedMacAppPath!)?.SequenceEqual(quarantine) == true
                    && MacQuarantine.Read(appZip)?.SequenceEqual(quarantine) == true, "Mac import preserves quarantine on the copy and source archive");
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                Check((File.GetUnixFileMode(Path.Combine(imported.PreparedMacAppPath!, "Contents", "MacOS", "Tiny")) & UnixFileMode.UserExecute) != 0, "import preserves executable Unix permission");
            await service.LaunchAsync(imported, cancellation).ConfigureAwait(false);
            Check(fakePlatform.OpenedApps.SequenceEqual(new[] { imported.PreparedMacAppPath! }), "launch dispatches the exact prepared app through the platform adapter");
            var played = (await service.LoadLibraryAsync(cancellation).ConfigureAwait(false)).Single(e => e.Id == imported.Id);
            Check(played.LastPlayedUtc is not null, "successful launch records the real launch request time");
            await service.RevealAsync(appZip, cancellation).ConfigureAwait(false);
            Check(fakePlatform.RevealedPaths.SequenceEqual(new[] { appZip }), "Finder reveal uses the exact source path");
            await service.OpenUrlAsync("https://dustore.ru/explore", cancellation).ConfigureAwait(false);
            Check(fakePlatform.OpenedUrls.Single() == "https://dustore.ru/explore", "store URL is passed to the native platform adapter");
            await MustThrowAsync<ArgumentException>(() => service.OpenUrlAsync("file:///etc/passwd", cancellation));
            checks.Add("external URLs are limited to HTTP and HTTPS");

            // Use the existing owned Tiny ZIP only as a service-commit fixture.
            var commitOriginal = await service.AddGameAsync(portable, cancellation).ConfigureAwait(false);
            commitOriginal = await service.SetWindowOptionsAsync(commitOriginal.Id, GameLaunchOptions.Windowed, 960, 540, cancellation).ConfigureAwait(false);
            string portableHash = HashSource(portable);
            var commitPackage = new PackageResult(appZip, "SmokeFixture", portableHash, "", "", []);
            string libraryBeforeCancel = HashSource(service.LibraryPath);
            await MustThrowAsync<OperationCanceledException>(() => service.CommitConvertedMacAsync(portable, commitPackage, cancelled.Token));
            var afterCancelledCommit = (await new LauncherServices(profileDirectory, fakePlatform).LoadLibraryAsync(cancellation).ConfigureAwait(false))
                .Single(e => e.Id == commitOriginal.Id);
            Check(HashSource(service.LibraryPath) == libraryBeforeCancel && afterCancelledCommit == commitOriginal,
                "pre-cancelled converted commit preserves the durable library and existing entry");

            var committed = await service.CommitConvertedMacAsync(portable, commitPackage, cancellation).ConfigureAwait(false);
            var durableCommit = (await new LauncherServices(profileDirectory, fakePlatform).LoadLibraryAsync(cancellation).ConfigureAwait(false))
                .Single(e => e.SourcePath == portable);
            Check(committed.Entry == (commitOriginal with { PreparedMacAppPath = committed.Entry.PreparedMacAppPath, LastOutputPath = committed.OutputPath }),
                "converted commit preserves the existing ID, source, options and other entry fields");
            Check(committed.OutputPath == Path.GetFullPath(appZip) && durableCommit == committed.Entry
                && committed.Entry.CanLaunchOnMac && committed.Entry.PreparedMacAppPath is not null
                && Directory.Exists(committed.Entry.PreparedMacAppPath)
                && File.Exists(Path.Combine(committed.Entry.PreparedMacAppPath, "Contents", "Info.plist"))
                && File.Exists(Path.Combine(committed.Entry.PreparedMacAppPath, "Contents", "MacOS", "Tiny")),
                "converted commit returns a prepared app receipt matching an independent durable reload");
            Check(HashSource(portable) == portableHash && HashSource(appZip) == archiveHash,
                "converted commit preserves the original input and completed ZIP bytes");

            string invalidCommitZip = Path.Combine(fixtures, "Invalid-commit.zip");
            File.WriteAllText(invalidCommitZip, "Owned smoke fixture: not a ZIP archive.");
            string invalidCommitHash = HashSource(invalidCommitZip);
            string libraryBeforeFailure = HashSource(service.LibraryPath);
            string preparedBeforeFailure = HashSource(committed.Entry.PreparedMacAppPath!);
            var invalidCommitPackage = new PackageResult(invalidCommitZip, "SmokeFixture", portableHash, "", "", []);
            await MustThrowAsync<InvalidDataException>(() => service.CommitConvertedMacAsync(portable, invalidCommitPackage, cancellation));
            var afterFailedCommit = (await new LauncherServices(profileDirectory, fakePlatform).LoadLibraryAsync(cancellation).ConfigureAwait(false))
                .Single(e => e.Id == committed.Entry.Id);
            Check(HashSource(service.LibraryPath) == libraryBeforeFailure && afterFailedCommit == committed.Entry
                && Directory.Exists(committed.Entry.PreparedMacAppPath)
                && HashSource(committed.Entry.PreparedMacAppPath!) == preparedBeforeFailure
                && HashSource(appZip) == archiveHash && HashSource(portable) == portableHash
                && HashSource(invalidCommitZip) == invalidCommitHash,
                "invalid converted ZIP preserves the previous durable entry, prepared app and ZIP bytes");

            string pe = Path.Combine(fixtures, "Windows.exe");
            File.WriteAllBytes(pe, new byte[] { 0x4D, 0x5A, 0, 0 });
            var windowsEntry = await service.AddGameAsync(pe, cancellation).ConfigureAwait(false);
            await MustThrowAsync<InvalidOperationException>(() => service.LaunchAsync(windowsEntry, cancellation));
            Check(fakePlatform.OpenedApps.Count == 1, "Windows EXE is not executed as a Mac app without conversion");

            string traversal = Path.Combine(fixtures, "Traversal.zip");
            WriteZip(traversal, ("Tiny.app/Contents/Info.plist", [1], 0x81A4), ("Tiny.app/Contents/MacOS/Tiny", [1], 0x81ED), ("../escape", [1], 0x81A4));
            await MustThrowAsync<InvalidDataException>(() => service.AddGameAsync(traversal, cancellation));
            Check(!File.Exists(Path.Combine(profileDirectory, "escape")), "archive traversal is rejected before extraction");
            string linkEscape = Path.Combine(fixtures, "LinkEscape.zip");
            WriteZip(linkEscape, ("Tiny.app/Contents/Info.plist", [1], 0x81A4), ("Tiny.app/Contents/MacOS/Tiny", [1], 0x81ED),
                ("Tiny.app/Contents/Resources/outside", Encoding.UTF8.GetBytes("../../../../escape"), 0xA1FF));
            await MustThrowAsync<InvalidDataException>(() => service.AddGameAsync(linkEscape, cancellation));
            checks.Add("escaping symbolic links are rejected before extraction");
            string collision = Path.Combine(fixtures, "CaseCollision.zip");
            WriteZip(collision, ("Tiny.app/Contents/Info.plist", [1], 0x81A4), ("Tiny.app/Contents/MacOS/Tiny", [1], 0x81ED),
                ("Tiny.app/Contents/Resources/A", [1], 0x81A4), ("Tiny.app/Contents/Resources/a", [2], 0x81A4));
            await MustThrowAsync<InvalidDataException>(() => service.AddGameAsync(collision, cancellation));
            checks.Add("case-insensitive archive collisions are rejected");

            await service.RemoveGameAsync(imported.Id, cancellation).ConfigureAwait(false);
            Check(File.Exists(appZip) && Directory.Exists(imported.PreparedMacAppPath), "removing a shortcut never deletes game files");
            await MustThrowAsync<KeyNotFoundException>(() => service.LaunchAsync(imported, cancellation));
            Check(fakePlatform.OpenedApps.Count == 1, "removed shortcuts cannot launch a stale game path");
            await service.RemoveGameAsync(windowsEntry.Id, cancellation).ConfigureAwait(false);
            Check(!(await new LauncherServices(profileDirectory).LoadLibraryAsync(cancellation).ConfigureAwait(false)).Any(e => e.Id == windowsEntry.Id || e.Id == imported.Id), "removals survive a profile reload");

            string corruptProfile = Path.Combine(fixtures, "corrupt-profile");
            Directory.CreateDirectory(corruptProfile);
            string corruptFile = Path.Combine(corruptProfile, "library.json");
            File.WriteAllText(corruptFile, "{ malformed library");
            string corruptHash = HashSource(corruptFile);
            await MustThrowAsync<InvalidDataException>(() => new LauncherServices(corruptProfile).AddGameAsync(portable, cancellation));
            Check(HashSource(corruptFile) == corruptHash, "a corrupt library is preserved rather than overwritten");
            sourcePreserved = HashSource(selected) == before;
            Check(sourcePreserved, "original game input remains byte-for-byte unchanged");
            Check(fakePlatform.OpenedApps.Count == 1, "smoke checks never execute a game; launch requests use the recording adapter");
            return new LauncherSmokeReport(true, checks, profileDirectory, inputPath, sourcePreserved, PreparedMacAppPath: prepared, SessionVerification: sessionVerification);

            void Check(bool condition, string description)
            {
                if (!condition) throw new InvalidOperationException("Smoke check failed: " + description);
                checks.Add(description);
            }
        }
        catch (Exception ex)
        {
            return new LauncherSmokeReport(false, checks, profileDirectory, inputPath, sourcePreserved, ex.GetType().Name + ": " + ex.Message, prepared, sessionVerification);
        }
    }

    private static void VerifyWindowArguments(Action<bool, string> check)
    {
        var previousDisplay = UltraMode.Display;
        var previousWorkArea = UltraMode.WorkingArea;
        try
        {
            UltraMode.Display = (1440, 900);
            UltraMode.WorkingArea = (1440, 850);
            check(GameLaunchOptions.Arguments(GameEngineKind.Unity, null, null, null).SequenceEqual(
                new[] { "-screen-fullscreen", "0", "-window-mode", "windowed", "-screen-width", "1280", "-screen-height", "720" }),
                "first Unity launch uses a manageable window rather than capturing the entire screen");
            check(GameLaunchOptions.Arguments(GameEngineKind.Unity, GameLaunchOptions.Fullscreen, null, null).SequenceEqual(
                new[] { "-screen-fullscreen", "1", "-window-mode", "borderless", "-screen-width", "1440", "-screen-height", "900" }),
                "explicit fullscreen keeps the full logical display size");
            foreach (var area in new[] { (Width: 800, Height: 480), (Width: 1024, Height: 640), (Width: 1280, Height: 720), (Width: 1440, Height: 850), (Width: 3440, Height: 1400) })
            {
                UltraMode.WorkingArea = area;
                var fitted = GameLaunchOptions.FitWindow(5000, 3000, area.Width, area.Height);
                check(fitted.Width > 0 && fitted.Height > 0 && fitted.Width <= area.Width - 32 && fitted.Height <= area.Height - 96
                    && Math.Abs(fitted.Width - fitted.Height * (5000D / 3000)) < 2,
                    $"5000x3000 window fits {area.Width}x{area.Height} usable points with chrome/exit space and preserved aspect ratio");
                var unity = GameLaunchOptions.Arguments(GameEngineKind.Unity, GameLaunchOptions.Windowed, 5000, 3000);
                var godot = GameLaunchOptions.Arguments(GameEngineKind.Godot, GameLaunchOptions.Windowed, 5000, 3000);
                check(unity.Contains(fitted.Width.ToString()) && unity.Contains(fitted.Height.ToString())
                    && godot.SequenceEqual(new[] { "--windowed", "--resolution", $"{fitted.Width}x{fitted.Height}" }),
                    "both engine launch paths receive the fitted resolution for " + area.Width + "x" + area.Height);
            }
            UltraMode.WorkingArea = (1440, 850);
            check(GameLaunchOptions.FitWindow(1024, 576, 1440, 850) == (1024, 576), "a fitting custom window is never enlarged or rescaled");
            check(GameLaunchOptions.FitWindow(0, -1, 1440, 850) == (1280, 720), "invalid persisted dimensions fall back to a valid fitting window");
            check(GameLaunchOptions.Arguments(GameEngineKind.Other, null, 5000, 3000).Count == 0
                && GameLaunchOptions.Arguments(GameEngineKind.Unity, GameLaunchOptions.GameDefault, 5000, 3000).Count == 0,
                "unknown engines and explicit game-default mode do not receive invented engine arguments");
        }
        finally { UltraMode.Display = previousDisplay; UltraMode.WorkingArea = previousWorkArea; }
    }

    private static async Task VerifyCaptureConfigurationAsync(string fixtures, CancellationToken cancellation, Action<bool, string> check)
    {
        string prefix = Path.Combine(fixtures, "display-capture-owned-prefix");
        string bin = Path.Combine(fixtures, "display-capture-owned-runtime", "bin");
        Directory.CreateDirectory(prefix); Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(prefix, "system.reg"), "Owned recording fixture, never executed.");
        File.WriteAllText(Path.Combine(prefix, ".dustore-display-v1"), "Unverified legacy marker must not suppress the repair.");
        File.WriteAllText(Path.Combine(bin, "wineserver"), "Owned recording fixture, never executed.");
        string wine = Path.Combine(bin, "wine"), marker = Path.Combine(prefix, ".dustore-display-v2");
        int calls = 0;
        Task<(int Code, string Output)> Failed(string tool, IEnumerable<string> arguments, IDictionary<string, string>? environment, TimeSpan timeout, CancellationToken token)
        { calls++; return Task.FromResult((5, "Owned command rejection.")); }
        await MustThrowAsync<IOException>(() => GameLaunchOptions.ConfigureDisplayCaptureAsync(wine, prefix, cancellation, Failed));
        check(calls == 1 && !File.Exists(marker), "failed capture command cannot write a success marker, even when an old marker exists");
        Task<(int Code, string Output)> WrongReadback(string tool, IEnumerable<string> arguments, IDictionary<string, string>? environment, TimeSpan timeout, CancellationToken token)
        {
            calls++;
            var args = arguments.ToArray();
            return Task.FromResult((0, args.Contains("query") ? args[Array.IndexOf(args, "/v") + 1] + "    REG_SZ    y\n" : ""));
        }
        await MustThrowAsync<IOException>(() => GameLaunchOptions.ConfigureDisplayCaptureAsync(wine, prefix, cancellation, WrongReadback));
        check(!File.Exists(marker), "successful reg exit with capture still enabled is rejected by actual readback parsing");
        int successfulCommands = 0;
        Task<(int Code, string Output)> Recorded(string tool, IEnumerable<string> arguments, IDictionary<string, string>? environment, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); successfulCommands++;
            check(environment?["WINEPREFIX"] == prefix, "capture preparation is limited to the exact owned prefix");
            var args = arguments.ToArray();
            return Task.FromResult((0, args.Contains("query") ? args[Array.IndexOf(args, "/v") + 1] + "    REG_SZ    n\r\n" : ""));
        }
        await GameLaunchOptions.ConfigureDisplayCaptureAsync(wine, prefix, cancellation, Recorded);
        check(successfulCommands == 5 && File.ReadAllText(marker) == "version=2\nCaptureDisplaysForFullscreen=n\nUseFullscreenSpace=n\n",
            "both capture flags are read back before the owned server finishes and the new marker commits");
        await GameLaunchOptions.ConfigureDisplayCaptureAsync(wine, prefix, cancellation, Recorded);
        check(successfulCommands == 5, "a verified unchanged prefix does not repeat capture preparation on the next launch");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        string cancelledPrefix = Path.Combine(fixtures, "display-capture-cancelled");
        await MustThrowAsync<OperationCanceledException>(() => GameLaunchOptions.ConfigureDisplayCaptureAsync(wine, cancelledPrefix, cancelled.Token, Recorded));
        check(!Directory.Exists(cancelledPrefix) && successfulCommands == 5, "cancelled preparation neither starts a command nor creates a prefix or marker");
        check(!Directory.EnumerateFiles(prefix, ".dustore-display-v2.*.tmp").Any(), "capture preparation leaves no unfinished marker file");
    }

    private static async Task MustThrowAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().ConfigureAwait(false); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected rejection: " + typeof(T).Name);
    }

    private static void WriteZip(string path, params (string Name, byte[] Bytes, int Mode)[] entries)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Name);
            entry.ExternalAttributes = item.Mode << 16;
            using var output = entry.Open();
            output.Write(item.Bytes);
        }
    }

    private static string HashSource(string path)
    {
        if (File.Exists(path)) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        HashDirectory(path, "");
        return Convert.ToHexString(hash.GetHashAndReset());

        void HashDirectory(string directory, string relative)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                string name = relative + Path.GetFileName(entry);
                hash.AppendData(Encoding.UTF8.GetBytes(name + "\0"));
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    hash.AppendData(Encoding.UTF8.GetBytes(new FileInfo(entry).LinkTarget ?? ""));
                else if ((attributes & FileAttributes.Directory) != 0) HashDirectory(entry, name + "/");
                else { using var input = File.OpenRead(entry); hash.AppendData(SHA256.HashData(input)); }
            }
        }
    }

    private sealed class RecordingPlatform : IPlatformLauncher
    {
        public bool IsMacOS => true;
        public List<string> OpenedApps { get; } = [];
        public List<string> RevealedPaths { get; } = [];
        public List<string> OpenedUrls { get; } = [];
        public Task OpenAppAsync(string path, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, CancellationToken cancellation = default) { cancellation.ThrowIfCancellationRequested(); OpenedApps.Add(path); return Task.CompletedTask; }
        public Task RevealAsync(string path, CancellationToken cancellation = default) { cancellation.ThrowIfCancellationRequested(); RevealedPaths.Add(path); return Task.CompletedTask; }
        public Task OpenUrlAsync(string url, CancellationToken cancellation = default) { cancellation.ThrowIfCancellationRequested(); OpenedUrls.Add(url); return Task.CompletedTask; }
    }
}
