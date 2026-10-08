using System.Diagnostics;
using DustoreX.AutoConverter;

namespace DustoreLauncherV.Mac.Services;

/// <summary>
/// Mac CI check of the whole Wine path: the launcher installs Wine, eX picks the game EXE out of a
/// Unity-style folder and packages it, and the package's own launch script runs it through that Wine.
/// The "game" is Wine's own cmd.exe (a real Windows PE) copied beside a decoy crash handler.
/// </summary>
internal static class WineSmoke
{
    public const string Token = "DUSTORE-WINE-OK";

    public static async Task<object> RunAsync(string workDirectory, CancellationToken cancellation)
    {
        var total = Stopwatch.StartNew();
        var install = Stopwatch.StartNew();
        bool wasInstalled = WineRuntime.IsInstalled;
        string wine = await WineRuntime.InstallAsync(new Progress<WineProgress>(p => Console.WriteLine(p.Stage + " " + (p.Fraction * 100).ToString("0"))), cancellation);
        install.Stop();
        var (versionCode, versionText) = await WineRuntime.RunAsync(wine, new[] { "--version" }, null, TimeSpan.FromMinutes(2), cancellation);
        if (versionCode != 0) throw new InvalidOperationException("wine --version failed: " + versionText);
        var dxvk = WineRuntime.InstalledDxvkLibraries();
        if (!dxvk.Contains("x86_64-windows/d3d11.dll")) throw new InvalidOperationException("DXVK d3d11.dll was not installed into Wine: " + string.Join(", ", dxvk));

        string wineHome = Path.GetDirectoryName(Path.GetDirectoryName(wine)!)!;
        string cmd = Directory.EnumerateFiles(wineHome, "cmd.exe", SearchOption.AllDirectories)
            .FirstOrDefault(p => p.Contains("x86_64-windows", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Wine has no x86_64-windows/cmd.exe to use as a test program.");

        string game = Path.Combine(workDirectory, "wine-smoke-game-" + Guid.NewGuid().ToString("N"), "WineSmoke");
        Directory.CreateDirectory(Path.Combine(game, "WineSmoke_Data"));
        File.Copy(cmd, Path.Combine(game, "WineSmoke.exe"));
        File.Copy(cmd, Path.Combine(game, "UnityCrashHandler64.exe")); // decoy the finder must skip
        File.WriteAllText(Path.Combine(game, "WineSmoke_Data", "globalgamemanagers"), "\0\0\0\02022.3.10f1\0synthetic");

        var plan = ConversionEngine.Inspect(game, TargetPlatform.MacOS);
        if (plan.Method != "wine" || !plan.CanConvert || !plan.Detail.Contains("WineSmoke.exe", StringComparison.Ordinal))
            throw new InvalidOperationException("eX did not plan the Unity-style folder for Wine with WineSmoke.exe: " + plan.Detail);
        string package = Path.Combine(workDirectory, "wine-smoke-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        await ConversionEngine.ConvertAsync(new ConversionRequest(game, TargetPlatform.MacOS, "WineSmoke", package), null, cancellation);
        string apps = Path.Combine(workDirectory, "wine-smoke-apps", Guid.NewGuid().ToString("N"));
        string app = MacPackageImporter.ImportIfMacApp(package, apps, cancellation)
            ?? throw new InvalidOperationException("The Wine package has no .app.");
        if (!WineRuntime.IsWineWrapper(app)) throw new InvalidOperationException("The package is not recognised as a Wine wrapper.");

        // The package's own script must find the launcher-installed Wine without any hint.
        var run = Stopwatch.StartNew();
        // Errors and DLL loading are traced for the check only; players keep WINEDEBUG=-all.
        var environment = new Dictionary<string, string>
        {
            ["DUSTOREX_WINE"] = "", ["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin", ["WINEDEBUG"] = "err+all,warn+module,fixme-all"
        };
        var (code, output) = await WineRuntime.RunAsync(Path.Combine(app, "Contents", "MacOS", "launch"),
            new[] { "/c", "echo", Token }, environment, TimeSpan.FromMinutes(10), cancellation);
        run.Stop();
        await File.WriteAllTextAsync(Path.Combine(workDirectory, "wine-smoke-output.txt"),
            $"exit {code}\nwine {versionText.Trim()}\nhome {wineHome}\n\n{output}", cancellation);
        bool token = output.Contains(Token, StringComparison.Ordinal);
        if (!token || code != 0) throw new InvalidOperationException($"The packaged game did not run under Wine (exit {code}): " + Tail(output));
        var directEntry = new GameEntry(Guid.NewGuid(), "WineSmoke", game, DateTimeOffset.UtcNow, PreparedMacAppPath: app, Ultra: true);
        string directLog = Path.Combine(workDirectory, "wine-smoke-direct.log");
        var directEnvironment = GameLaunchOptions.Environment(true, workDirectory);
        directEnvironment[PlatformLauncher.GameLogKey] = directLog;
        if (Edition.IsPrime) UltraMode.AddDxvk(directEnvironment, workDirectory);
        object directSession = await VerifySessionAsync(directEntry, directLog, workDirectory,
            () => WineGameSession.LaunchAsync(app, directEntry, ["/c", "echo", Token], directEnvironment, workDirectory, cancellation), cancellation);
        object? prime = null;
        if (Edition.IsPrime && UltraMode.UsesMetal)
        {
            // Maximum performance: CrossOver-based Wine with DXMT must install and run Windows code.
            var primeInstall = Stopwatch.StartNew();
            await PrimeGraphics.InstallAsync(new Progress<WineProgress>(p => Console.WriteLine("prime " + p.Stage)), cancellation);
            var libraries = PrimeGraphics.InstalledLibraries();
            if (!libraries.Contains("x86_64-windows/d3d11.dll") || !libraries.Contains("x86_64-unix/winemetal.so"))
                throw new InvalidOperationException("DXMT was not installed into the Prime Wine: " + string.Join(", ", libraries));
            string metalPrefix = Path.Combine(workDirectory, "wine-smoke-metal-prefix");
            var metalEnv = new Dictionary<string, string> { ["WINEPREFIX"] = metalPrefix, ["WINEDEBUG"] = "-all", ["WINEDLLOVERRIDES"] = "mscoree,mshtml=" };
            await WineRuntime.RunAsync(PrimeGraphics.WineBinary, new[] { "wineboot", "--init" }, metalEnv, TimeSpan.FromMinutes(10), cancellation);
            await WineRuntime.RunAsync(PrimeGraphics.WineServer, new[] { "-w" }, metalEnv, TimeSpan.FromMinutes(10), cancellation);
            var (metalCode, metalOutput) = await WineRuntime.RunAsync(PrimeGraphics.WineBinary, new[] { "cmd", "/c", "echo", Token }, metalEnv, TimeSpan.FromMinutes(5), cancellation);
            if (!metalOutput.Contains(Token, StringComparison.Ordinal) || metalCode != 0)
                throw new InvalidOperationException($"The Prime Wine did not run Windows code (exit {metalCode}): " + Tail(metalOutput));
            var metalEntry = directEntry with { Id = Guid.NewGuid() };
            string metalLog = Path.Combine(GameLaunchOptions.LogsDirectory(), metalEntry.Id.ToString("N") + "-metal.log");
            object metalSession = await VerifySessionAsync(metalEntry, metalLog, Path.Combine(WineRuntime.Root, "prime"),
                () => PrimeGraphics.LaunchAsync(app, metalEntry, ["/c", "echo", Token], cancellation), cancellation);
            prime = new { dxmtLibraries = libraries, installSeconds = Math.Round(primeInstall.Elapsed.TotalSeconds, 1), ranWindowsCode = true,
                directSession = metalSession, graphicsDeviceCreated = false, gameFpsMeasured = false };
        }
        return new
        {
            status = "Pass", edition = Edition.Name, primeMetal = prime, wineInstalledByLauncher = !wasInstalled, wineVersion = versionText.Trim(), wineArchive = WineRuntime.ArchiveName,
            wineSha256 = WineRuntime.Sha256, dxvkVersion = WineRuntime.DxvkVersion, dxvkLibraries = dxvk, installSeconds = Math.Round(install.Elapsed.TotalSeconds, 1), chosenExecutable = "WineSmoke.exe",
            decoySkipped = true, packagedRunExitCode = code, tokenSeen = token, runSeconds = Math.Round(run.Elapsed.TotalSeconds, 1),
            totalSeconds = Math.Round(total.Elapsed.TotalSeconds, 1), appleSilicon = WineRuntime.IsAppleSilicon,
            directSession, graphicsDeviceCreated = false, gameFpsMeasured = false,
            scope = "Generated Windows command fixture only; package script and scoped direct Wine sessions. No D3D device or game benchmark."
        };
    }

    private static async Task<object> VerifySessionAsync(GameEntry entry, string log, string diagnosticsDirectory, Func<Task> launch, CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource<GameSessionEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<GameSessionEventArgs> observer = (_, result) =>
        {
            if (result.Id == entry.Id && (result.ExitCode is not null || result.Error is not null)) completion.TrySetResult(result);
        };
        GameSessions.Changed += observer;
        var watch = Stopwatch.StartNew();
        try
        {
            await launch();
            var result = await completion.Task.WaitAsync(TimeSpan.FromMinutes(3), cancellation);
            string output = File.Exists(log + ".stdout") ? await File.ReadAllTextAsync(log + ".stdout", cancellation) : "";
            if (result.ExitCode != 0 || result.Error is not null || !output.Contains(Token, StringComparison.Ordinal) || GameSessions.Find(entry.Id) is not null)
                throw new InvalidOperationException($"Direct {result.Route} session failed: exit {result.ExitCode}; {result.Error}; " + Tail(output));
            string diagnostic = Path.Combine(diagnosticsDirectory, "Performance", entry.Id.ToString("N") + ".json");
            using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(diagnostic, cancellation));
            var root = document.RootElement;
            return new { route = result.Route, processId = root.GetProperty("processId").GetInt32(), exitCode = result.ExitCode,
                applicationResourcePolicyRequested = root.GetProperty("applicationResourcePolicyRequested").GetBoolean(),
                tokenSeen = true, actualProcessAndPrefixLifetimeObserved = true, sessionRemovedAfterExit = true,
                logBytes = new FileInfo(log).Length, elapsedSeconds = watch.Elapsed.TotalSeconds,
                graphicsDeviceCreated = false, gameFpsMeasured = false };
        }
        finally
        {
            GameSessions.Changed -= observer;
            if (GameSessions.Find(entry.Id) is { } active) await active.StopAsync(CancellationToken.None);
        }
    }

    private static string Tail(string text) => text.Length > 1500 ? text[^1500..] : text;
}
