using System.Text.RegularExpressions;

namespace DustoreLauncherV.Mac.Services;

public enum GameEngineKind { Other, Unity, Godot }

/// <summary>
/// Per-game window settings passed on the command line. Unity and Godot accept a window mode and
/// size there, which also overrides what a game saved on its first, too-large fullscreen start.
/// </summary>
public static class GameLaunchOptions
{
    public const string Windowed = "windowed", Fullscreen = "fullscreen", GameDefault = "game";
    public const int DefaultWidth = 1280, DefaultHeight = 720;

    public static GameEngineKind DetectEngine(string? app)
    {
        if (app is null || !Directory.Exists(app)) return GameEngineKind.Other;
        try
        {
            // An eX Wine package keeps the Windows build under Contents/Resources/game.
            string wineGame = Path.Combine(app, "Contents", "Resources", "game");
            string root = Directory.Exists(wineGame) ? wineGame : Path.Combine(app, "Contents");
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
            foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", options))
            {
                string name = Path.GetFileName(path);
                if (name.Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("UnityPlayer.dylib", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("globalgamemanagers", StringComparison.OrdinalIgnoreCase) || name.Equals("data.unity3d", StringComparison.OrdinalIgnoreCase))
                    return GameEngineKind.Unity;
                if (name.EndsWith(".pck", StringComparison.OrdinalIgnoreCase)) return GameEngineKind.Godot;
            }
            // Godot often embeds its .pck into the Windows exe: the file then ends with "GDPC".
            if (Directory.Exists(wineGame))
                foreach (string exe in Directory.EnumerateFiles(wineGame, "*.exe", SearchOption.TopDirectoryOnly))
                    if (EndsWithGodotPack(exe)) return GameEngineKind.Godot;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return GameEngineKind.Other;
    }

    private static bool EndsWithGodotPack(string exe)
    {
        try
        {
            using var stream = File.OpenRead(exe);
            if (stream.Length < 16) return false;
            stream.Seek(-4, SeekOrigin.End);
            Span<byte> tail = stackalloc byte[4];
            stream.ReadExactly(tail);
            return tail.SequenceEqual("GDPC"u8);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Godot 4 under Wine on a Mac: its Vulkan renderer goes through MoltenVK, which on Intel
    /// graphics cannot build Godot's compute pipelines (vkCreateComputePipelines error -3) and the
    /// game shows a black screen. The Compatibility renderer (OpenGL) works there, so Wine Godot
    /// games start with it.
    /// </summary>
    public static IReadOnlyList<string> WineGodotRenderer => new[] { "--rendering-driver", "opengl3", "--rendering-method", "gl_compatibility" };

    /// <summary>
    /// The first launch uses a window that fits the current usable screen in logical points.
    /// Explicit fullscreen and the game's own mode remain available in per-game settings.
    /// </summary>
    public static IReadOnlyList<string> Arguments(GameEngineKind engine, string? mode, int? width, int? height)
    {
        mode ??= Windowed;
        if (mode == GameDefault || engine == GameEngineKind.Other) return Array.Empty<string>();
        bool screenSize = mode == Fullscreen && width is null;
        int w = screenSize ? UltraMode.Display.Width : width ?? DefaultWidth, h = screenSize ? UltraMode.Display.Height : height ?? DefaultHeight;
        bool full = mode == Fullscreen;
        if (!full) (w, h) = FitWindow(w, h, UltraMode.WorkingArea.Width, UltraMode.WorkingArea.Height);
        return engine switch
        {
            GameEngineKind.Unity => full
                ? new[] { "-screen-fullscreen", "1", "-window-mode", "borderless", "-screen-width", w.ToString(), "-screen-height", h.ToString() }
                : new[] { "-screen-fullscreen", "0", "-window-mode", "windowed", "-screen-width", w.ToString(), "-screen-height", h.ToString() },
            GameEngineKind.Godot => new[] { full ? "--fullscreen" : "--windowed", "--resolution", $"{w}x{h}" },
            _ => Array.Empty<string>()
        };
    }

    /// <summary>Keep the requested aspect ratio while reserving space for window chrome and exit controls.</summary>
    internal static (int Width, int Height) FitWindow(int width, int height, int workingWidth, int workingHeight)
    {
        width = width > 0 ? width : DefaultWidth;
        height = height > 0 ? height : DefaultHeight;
        int availableWidth = Math.Max(1, (workingWidth > 0 ? workingWidth : DefaultWidth + 32) - 32);
        int availableHeight = Math.Max(1, (workingHeight > 0 ? workingHeight : DefaultHeight + 96) - 96);
        double factor = Math.Min(1D, Math.Min((double)availableWidth / width, (double)availableHeight / height));
        return (Math.Min(availableWidth, Math.Max(1, (int)Math.Floor(width * factor))),
            Math.Min(availableHeight, Math.Max(1, (int)Math.Floor(height * factor))));
    }

    /// <summary>
    /// Wine games: DXVK writes what device it created into the launcher's game logs, and MoltenVK
    /// recovers a lost device instead of leaving a black window. (DXVK_ASYNC is gone: with this
    /// DXVK build on Intel graphics it left frames empty.)
    /// </summary>
    public static Dictionary<string, string> Environment(bool wine, string logs) => wine
        ? new Dictionary<string, string> { ["DXVK_LOG_LEVEL"] = "info", ["DXVK_LOG_PATH"] = logs, ["MVK_CONFIG_RESUME_LOST_DEVICE"] = "1" }
        : new Dictionary<string, string>();

    /// <summary>~/Library/Logs/DUSTORE Launcher V/Games — one log per game, overwritten on each start.</summary>
    public static string LogsDirectory()
    {
        string folder = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "Library", "Logs", "DUSTORE Launcher V", "Games");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Stops a game even when it covers the screen and ignores Esc.</summary>
    public static async Task StopAsync(string app, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (WineRuntime.IsWineWrapper(app) && WinePrefixOf(app) is { } prefix)
        {
            string wineserver = Path.Combine(WineRuntime.CurrentLink, "bin", "wineserver");
            if (File.Exists(wineserver))
                await WineRuntime.RunAsync(wineserver, new[] { "-k" }, new Dictionary<string, string> { ["WINEPREFIX"] = prefix }, TimeSpan.FromSeconds(20), cancellation);
            return;
        }
        string pattern = Regex.Replace(Path.Combine(app.TrimEnd('/'), "Contents", "MacOS") + "/", @"[.\[\]()*+?{}|^$\\]", m => "\\" + m.Value);
        await WineRuntime.RunAsync("/usr/bin/pkill", new[] { "-f", "--", pattern }, null, TimeSpan.FromSeconds(10), cancellation);
    }

    /// <summary>
    /// Wine on macOS «captures» the display for a fullscreen game: ⌘Tab, the Dock and the launcher
    /// are unreachable and the game cannot be closed. With the capture off the game is still
    /// fullscreen, but ⌘Tab, ⌘Q and the Dock work. Written once per prefix.
    /// </summary>
    public static async Task ReleaseDisplayCaptureAsync(string wine, string prefix, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsMacOS()) return;
        await ConfigureDisplayCaptureAsync(wine, prefix, cancellation, WineRuntime.RunAsync).ConfigureAwait(false);
    }

    internal delegate Task<(int Code, string Output)> CaptureCommandRunner(string tool, IEnumerable<string> arguments,
        IDictionary<string, string>? environment, TimeSpan timeout, CancellationToken cancellation);

    internal static async Task ConfigureDisplayCaptureAsync(string wine, string prefix, CancellationToken cancellation,
        CaptureCommandRunner run)
    {
        const string contents = "version=2\nCaptureDisplaysForFullscreen=n\nUseFullscreenSpace=n\n";
        string marker = Path.Combine(prefix, ".dustore-display-v2");
        cancellation.ThrowIfCancellationRequested();
        if (File.Exists(marker) && await File.ReadAllTextAsync(marker, cancellation).ConfigureAwait(false) == contents) return;
        var env = new Dictionary<string, string> { ["WINEPREFIX"] = prefix, ["WINEDEBUG"] = "-all", ["WINEDLLOVERRIDES"] = "mscoree,mshtml=" };
        Directory.CreateDirectory(prefix);
        if (!File.Exists(Path.Combine(prefix, "system.reg")))
            await Checked(wine, new[] { "wineboot", "--init" }, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        foreach (string name in new[] { "CaptureDisplaysForFullscreen", "UseFullscreenSpace" })
        {
            await Checked(wine, new[] { "reg", "add", @"HKCU\Software\Wine\Mac Driver", "/v", name, "/t", "REG_SZ", "/d", "n", "/f" }, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            string output = await Checked(wine, new[] { "reg", "query", @"HKCU\Software\Wine\Mac Driver", "/v", name }, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            if (!Regex.IsMatch(output, @"(?im)^\s*" + Regex.Escape(name) + @"\s+REG_SZ\s+n\s*$"))
                throw new IOException("Wine не подтвердил безопасный режим экрана: " + name + ". Повторите запуск из лаунчера.");
        }
        string server = Path.Combine(Path.GetDirectoryName(wine)!, "wineserver");
        if (File.Exists(server)) await Checked(server, new[] { "-w" }, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        string temporary = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, contents, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, marker, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }

        async Task<string> Checked(string tool, IEnumerable<string> arguments, TimeSpan timeout)
        {
            cancellation.ThrowIfCancellationRequested();
            var result = await run(tool, arguments, env, timeout, cancellation).ConfigureAwait(false);
            if (result.Code != 0)
                throw new IOException("Не удалось настроить доступный выход из Wine (код " + result.Code + "). " +
                    (result.Output.Length > 500 ? result.Output[^500..] : result.Output));
            return result.Output;
        }
    }

    // The eX wrapper script names its prefix: export WINEPREFIX="$HOME/Library/Application Support/DustoreX/Wine/<id>"
    internal static string? WinePrefixOf(string app)
    {
        string script = Path.Combine(app, "Contents", "MacOS", "launch");
        if (!File.Exists(script)) return null;
        var match = Regex.Match(File.ReadAllText(script), "export WINEPREFIX=\"\\$HOME/([^\"]+)\"");
        return match.Success ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), match.Groups[1].Value) : null;
    }
}
