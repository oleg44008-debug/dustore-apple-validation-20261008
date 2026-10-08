using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace DustoreLauncherV.Mac.Services;

/// <summary>Smoke-only real child-process checks. No game binaries or user prefixes are used.</summary>
internal static class SessionChecks
{
    internal static int RunFixture(string[] args)
    {
        if (args.Contains("--hold"))
        {
            string ready = args[Array.IndexOf(args, "--ready") + 1];
            File.WriteAllText(ready, "READY");
            Thread.Sleep(30_000);
            return 0;
        }
        Console.WriteLine("DUSTORE-SESSION-FIXTURE " + (Environment.GetEnvironmentVariable("DXVK_STATE_CACHE") ?? "missing")
            + " " + (Environment.GetEnvironmentVariable("DXMT_CONFIG") ?? "absent"));
        Console.Out.Flush();
        byte[] bytes = Encoding.ASCII.GetBytes(new string('x', 16_384));
        using Stream stdout = Console.OpenStandardOutput(), stderr = Console.OpenStandardError();
        for (int i = 0; i < 192; i++) { stdout.Write(bytes); stderr.Write(bytes); }
        stdout.Flush(); stderr.Flush();
        return 7;
    }

    public static async Task<object> RunAsync(string fixtures, CancellationToken cancellation)
    {
        var checks = new List<string>();
        string directory = Path.Combine(fixtures, "owned-processes"); Directory.CreateDirectory(directory);
        string prefix = Path.Combine(directory, "prefix");
        var entry = new GameEntry(Guid.NewGuid(), "DUSTORE session fixture", directory, DateTimeOffset.UtcNow, Ultra: true);
        using (var pending = GameSessions.Reserve(entry.Id, prefix))
        {
            Check(Rejects(() => GameSessions.Reserve(entry.Id, prefix + "-other")), "pending launches reserve their game ID atomically");
            Check(Rejects(() => GameSessions.Reserve(Guid.NewGuid(), prefix)), "pending launches reserve their exact Wine prefix atomically");
            using var independent = GameSessions.Reserve(Guid.NewGuid(), prefix + "-independent");
            checks.Add("independent Wine prefixes remain available");
        }
        using (var released = GameSessions.Reserve(entry.Id, prefix)) checks.Add("failed or cancelled launch reservations are released");

        var child = OwnProcess();
        child.Environment["DXMT_CONFIG"] = "unrelated-renderer-profile";
        var childEnvironment = new Dictionary<string, string> { ["DXVK_STATE_CACHE"] = "1" };
        string? inherited = Environment.GetEnvironmentVariable("DXMT_CONFIG");
        bool wrapped = UltraMode.ConfigureGameProcess(child, Edition.IsPrime && entry.Ultra, childEnvironment);
        Check(!child.Environment.ContainsKey("DXMT_CONFIG") && Environment.GetEnvironmentVariable("DXMT_CONFIG") == inherited,
            "session graphics cleanup changes only the owned child's environment");
        Check(!wrapped || Edition.IsPrime && OperatingSystem.IsMacOS(), "Free never requests the ULTRA application policy");
        string log = Path.Combine(directory, "noisy-child.log");
        var exit = await RunObservedAsync(entry, prefix, log, child, cancellation);
        Check(exit.ExitCode == 7 && exit.Error is not null, "nonzero child exits are reported after the actual process finishes");
        Check(GameSessions.Find(entry.Id) is null, "completed sessions leave the active registry");
        Check(new FileInfo(log).Length == 2 * 1024 * 1024 && new FileInfo(log + ".stdout").Length == 2 * 1024 * 1024,
            "three megabytes from each pipe drain completely while both logs stop at two megabytes");
        Check(File.ReadLines(log + ".stdout").First().Contains("DUSTORE-SESSION-FIXTURE 1 absent", StringComparison.Ordinal),
            "the actual child received its scoped renderer profile");
        if (wrapped) checks.Add("native taskpolicy executed the owned child successfully; resource tiers and game FPS were not measured");

        var stoppable = entry with { Id = Guid.NewGuid(), Name = "DUSTORE stoppable fixture" };
        var hold = OwnProcess(); string ready = Path.Combine(directory, "ready.txt");
        hold.ArgumentList.Add("--hold"); hold.ArgumentList.Add("--ready"); hold.ArgumentList.Add(ready);
        var finished = new TaskCompletionSource<GameSessionEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<GameSessionEventArgs> observer = (_, update) => { if (update.Id == stoppable.Id && update.ExitCode is not null) finished.TrySetResult(update); };
        GameSessions.Changed += observer;
        GameSessions.WineSession? session = null;
        try
        {
            using var reservation = GameSessions.Reserve(stoppable.Id, prefix);
            var process = Process.Start(hold) ?? throw new IOException("Could not start the owned stoppable fixture.");
            GameSessions.Observe(process, stoppable, Path.Combine(directory, "no-server"), prefix, "owned fixture", Path.Combine(directory, "stoppable.log"), false, reservation);
            session = GameSessions.Find(stoppable.Id);
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(ready) && timeout.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20, cancellation);
            Check(File.Exists(ready) && session is not null, "the stoppable child is tracked for its actual lifetime");
            Check(Rejects(() => GameSessions.Reserve(Guid.NewGuid(), prefix)), "an active prefix rejects a second launch");
            await session!.StopAsync(cancellation);
            var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellation);
            Check(result.Error is null && GameSessions.Find(stoppable.Id) is null, "Stop targets only the tracked child and suppresses an expected termination error");
        }
        finally
        {
            GameSessions.Changed -= observer;
            if (GameSessions.Find(stoppable.Id) is not null && session is not null) await session.StopAsync(CancellationToken.None);
        }

        string wineHome = Path.Combine(directory, "capability-wine");
        string binary = Path.Combine(wineHome, "bin", "wine64"); Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        string ntdll = Path.Combine(wineHome, "lib", "wine", "x86_64-unix", "ntdll.so"); Directory.CreateDirectory(Path.GetDirectoryName(ntdll)!);
        byte[] fixtureLibrary = new byte[8 * 1024 * 1024]; "WINEMSYNC"u8.CopyTo(fixtureLibrary); File.WriteAllBytes(ntdll, fixtureLibrary);
        var environment = new Dictionary<string, string>();
        var cold = Stopwatch.StartNew(); UltraMode.AddCommon(environment, binary); cold.Stop();
        Check(environment["WINEMSYNC"] == "1", "msync is requested only when the selected Wine binary exposes its marker");
        var timings = new List<double>();
        for (int i = 0; i < 20; i++) { var watch = Stopwatch.StartNew(); UltraMode.AddCommon(environment, binary); watch.Stop(); timings.Add(watch.Elapsed.TotalMilliseconds); }
        File.WriteAllBytes(ntdll, "no matching backend"u8.ToArray());
        UltraMode.AddCommon(environment, binary);
        Check(environment["WINEMSYNC"] == "0", "changed Wine library metadata invalidates the capability cache");
        UltraMode.AddDxvk(environment, directory);
        string config = environment["DXVK_CONFIG_FILE"]; var prior = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(config, prior); UltraMode.AddDxvk(environment, directory);
        Check(File.GetLastWriteTimeUtc(config) == prior, "unchanged per-game DXVK policy is not rewritten on subsequent launches");
        return new { status = "Pass", checks, nativeMacChildExecution = OperatingSystem.IsMacOS(), taskpolicyWrapperExecuted = wrapped,
            noisyChildExitCode = exit.ExitCode, maximumLogBytes = 2 * 1024 * 1024, capabilityFixtureBytes = fixtureLibrary.Length,
            capabilityColdMilliseconds = cold.Elapsed.TotalMilliseconds, capabilityCachedMeanMilliseconds = timings.Average(),
            capabilityCachedMaxMilliseconds = timings.Max(), graphicsDeviceCreated = false, gameFpsMeasured = false };

        void Check(bool passed, string description)
        {
            if (!passed) throw new InvalidOperationException("Session check failed: " + description);
            checks.Add(description);
        }
    }

    private static bool Rejects(Func<IDisposable> reservation)
    {
        try { using var value = reservation(); return false; } catch (InvalidOperationException) { return true; }
    }

    private static ProcessStartInfo OwnProcess()
    {
        string executable = Environment.ProcessPath ?? throw new IOException("The current executable path is unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--session-fixture");
        return start;
    }

    private static async Task<GameSessionEventArgs> RunObservedAsync(GameEntry entry, string prefix, string log, ProcessStartInfo start, CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource<GameSessionEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<GameSessionEventArgs> observer = (_, update) => { if (update.Id == entry.Id && update.ExitCode is not null) completion.TrySetResult(update); };
        GameSessions.Changed += observer;
        try
        {
            using var reservation = GameSessions.Reserve(entry.Id, prefix);
            var process = Process.Start(start) ?? throw new IOException("Could not start the owned noisy fixture.");
            GameSessions.Observe(process, entry, Path.Combine(Path.GetDirectoryName(log)!, "no-server"), prefix, "owned fixture", log, false, reservation);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellation);
        }
        finally
        {
            GameSessions.Changed -= observer;
            if (GameSessions.Find(entry.Id) is { } running) await running.StopAsync(CancellationToken.None);
        }
    }
}
