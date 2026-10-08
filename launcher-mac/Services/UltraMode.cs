using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DustoreLauncherV.Mac.Services;

/// <summary>
/// Prime ULTRA applies measured-scope launch policies without altering the game's image quality.
/// Graphics backend, cache support and game-engine behavior determine the actual FPS benefit.
/// </summary>
public static class UltraMode
{
    /// <summary>Logical size of the main display, set by the window when it opens.</summary>
    public static (int Width, int Height) Display { get; set; } = (1440, 900);

    public static bool AppleSilicon => RuntimeInformation.OSArchitecture == Architecture.Arm64;
    private static readonly object CapabilityGate = new();
    private static readonly Dictionary<string, (long Length, DateTime Modified, bool Supported)> MsyncCapabilities = new(StringComparer.Ordinal);

    /// <summary>On Apple silicon ULTRA runs Windows games on the Metal route (DXMT).</summary>
    public static bool UsesMetal => AppleSilicon && OperatingSystem.IsMacOSVersionAtLeast(14);

    public static bool ShouldUseMetal(GameEngineKind engine, GameEntry entry)
        => UsesMetal && (entry.GraphicsMode == "metal" || entry.GraphicsMode is null or "auto" && engine == GameEngineKind.Unity);

    /// <summary>Engine arguments: none — the game keeps its resolution, quality and frame pacing.</summary>
    public static IReadOnlyList<string> Arguments(GameEngineKind engine) => Array.Empty<string>();

    private static string CacheDirectory(string kind)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string folder = OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Caches", "DUSTORE Launcher V", kind) : Path.Combine(Path.GetTempPath(), "dustore-" + kind);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Shared by both graphics routes.</summary>
    public static void AddCommon(IDictionary<string, string> env, string? wineBinary = null)
    {
        env["WINEDEBUG"] = "-all";
        // macOS does not provide Linux eventfd. Request only one supported sync backend.
        env["WINEESYNC"] = "0";
        env["WINEMSYNC"] = SupportsMsync(wineBinary ?? WineRuntime.WineBinary) ? "1" : "0";
        // macOS 15 Rosetta can advertise AVX/AVX2: games pick their vectorised code paths.
        if (AppleSilicon && OperatingSystem.IsMacOSVersionAtLeast(15)) env["ROSETTA_ADVERTISE_AVX"] = "1";
        env["MVK_CONFIG_RESUME_LOST_DEVICE"] = "1";
    }

    private static bool SupportsMsync(string? wineBinary)
    {
        if (wineBinary is null) return false;
        try
        {
            string home = Path.GetDirectoryName(Path.GetDirectoryName(wineBinary)!)!;
            foreach (string architecture in new[] { "x86_64-unix", "aarch64-unix" })
            {
                string ntdll = Path.Combine(home, "lib", "wine", architecture, "ntdll.so");
                var file = new FileInfo(ntdll);
                if (!file.Exists || file.Length >= 32 * 1024 * 1024) continue;
                lock (CapabilityGate)
                {
                    if (!MsyncCapabilities.TryGetValue(ntdll, out var cached) || cached.Length != file.Length || cached.Modified != file.LastWriteTimeUtc)
                    {
                        cached = (file.Length, file.LastWriteTimeUtc, File.ReadAllBytes(ntdll).AsSpan().IndexOf("WINEMSYNC"u8) >= 0);
                        MsyncCapabilities[ntdll] = cached;
                    }
                    if (cached.Supported) return true;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        return false;
    }

    /// <summary>DXVK 1.10 route: automatic compiler thread count, persistent state cache and unchanged image quality.</summary>
    public static void AddDxvk(IDictionary<string, string> env, string dataDirectory)
    {
        AddCommon(env);
        string config = Path.Combine(dataDirectory, "dxvk-ultra.conf");
        string contents = string.Join("\n",
            "# DUSTORE Prime ULTRA: steady frames, nothing taken away",
            "# Vertical sync stays: uncapped frames heat a MacBook until it throttles and loses FPS.",
            "dxvk.numCompilerThreads = 0",
            "");
        if (!File.Exists(config) || File.ReadAllText(config) != contents) File.WriteAllText(config, contents);
        env["DXVK_CONFIG_FILE"] = config;
        env["DXVK_STATE_CACHE"] = "1";
        env["DXVK_STATE_CACHE_PATH"] = CacheDirectory("dxvk");
        env["DXVK_FRAME_RATE"] = "0";
        env["DXVK_LOG_LEVEL"] = "none";
        env["DXVK_ASYNC"] = "0";
        // open --env cannot delete a variable: empty values reset an inherited renderer profile.
        foreach (string key in new[] { "DXMT_CONFIG", "DXMT_CONFIG_FILE", "DXMT_METALFX_SPATIAL_SWAPCHAIN", "D3DM_SUPPORT_ENVIRONMENT" }) env[key] = "";
    }

    /// <summary>Metal route (DXMT): native resolution, no upscaling, frames up to the display's maximum.</summary>
    public static void AddMetal(IDictionary<string, string> env, List<string> dxmtConfig)
    {
        AddCommon(env, PrimeGraphics.WineBinary);
        env["DXMT_SHADER_CACHE_PATH"] = CacheDirectory("dxmt");
        env["DXMT_SHADER_CACHE"] = "1";
        // Frame pacing stays the display's own.
    }

    /// <summary>Child-only policies. taskpolicy execs Wine with app resource policies and an unrestricted latency/throughput tier.</summary>
    public static bool ConfigureGameProcess(ProcessStartInfo start, bool ultra, IReadOnlyDictionary<string, string> environment)
    {
        foreach (string key in start.Environment.Keys.ToArray())
            if (key.StartsWith("DXVK_", StringComparison.Ordinal) || key.StartsWith("DXMT_", StringComparison.Ordinal)
                || key.StartsWith("D3DM_", StringComparison.Ordinal) || key is "WINEDLLOVERRIDES" or "WINEDLLPATH"
                or "WINEESYNC" or "WINEMSYNC" or "ROSETTA_ADVERTISE_AVX" or "MTL_HUD_ENABLED") start.Environment.Remove(key);
        foreach (var (key, value) in environment) start.Environment[key] = value;
        const string policy = "/usr/sbin/taskpolicy";
        if (!ultra || !OperatingSystem.IsMacOS() || !File.Exists(policy)) return false;
        string executable = start.FileName;
        string[] arguments = start.ArgumentList.ToArray();
        start.FileName = policy;
        start.ArgumentList.Clear();
        foreach (string argument in new[] { "-a", "-l", "0", "-t", "0", executable }.Concat(arguments)) start.ArgumentList.Add(argument);
        return true;
    }

    public static void WriteDiagnostics(string directory, GameEntry entry, string route, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, int? processId = null, bool applicationPolicy = false)
    {
        try
        {
            string folder = Path.Combine(directory, "Performance"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, entry.Id.ToString("N") + ".json"), JsonSerializer.Serialize(new
            {
                game = entry.Name, entry.Id, ultra = Edition.IsPrime && entry.Ultra, route, processId,
                applicationResourcePolicyRequested = applicationPolicy,
                nativeLaunchServicesPolicy = route == "native", arguments,
                gameEnvironment = environment.Where(pair => pair.Key.StartsWith("DX", StringComparison.Ordinal)
                    || pair.Key.StartsWith("WINE", StringComparison.Ordinal) || pair.Key.StartsWith("ROSETTA", StringComparison.Ordinal)
                    || pair.Key == "MTL_HUD_ENABLED").ToDictionary(pair => pair.Key, pair => pair.Value),
                qualityChanged = false, fpsMeasured = false, verifiedAtUtc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
