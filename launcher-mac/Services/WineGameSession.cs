using System.Diagnostics;
using DustoreX.AutoConverter;

namespace DustoreLauncherV.Mac.Services;

/// <summary>Launches the actual Wine game with child-only ULTRA policies and bounded lifetime helpers.</summary>
internal static class WineGameSession
{
    public static Task LaunchAsync(string app, GameEntry entry, IReadOnlyList<string> arguments,
        Dictionary<string, string> environment, string profile, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string wine = WineRuntime.WineBinary ?? throw new IOException("Wine не установлен.");
        string gameRoot = Path.Combine(app, "Contents", "Resources", "game");
        var executable = GameExecutableFinder.Find(gameRoot, entry.Name)
            ?? throw new InvalidDataException("В пакете не найден исполняемый файл игры.");
        string executablePath = Path.Combine(gameRoot, executable.Path.Replace('/', Path.DirectorySeparatorChar));
        environment["WINEPREFIX"] = GameLaunchOptions.WinePrefixOf(app)
            ?? throw new InvalidDataException("Не удалось определить окружение Wine этой игры.");
        using var reservation = GameSessions.Reserve(entry.Id, environment["WINEPREFIX"]);
        environment["WINEDLLOVERRIDES"] = "d3d11,dxgi,d3d10core=b;mscoree,mshtml=";
        string log = environment[PlatformLauncher.GameLogKey];
        var childEnvironment = environment.Where(pair => pair.Key != PlatformLauncher.GameLogKey).ToDictionary(pair => pair.Key, pair => pair.Value);
        var start = new ProcessStartInfo(wine)
        {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.GetFileName(executablePath));
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        bool ultra = Edition.IsPrime && entry.Ultra;
        bool policy = UltraMode.ConfigureGameProcess(start, ultra, childEnvironment);
        var game = Process.Start(start) ?? throw new IOException("Не удалось запустить Wine.");
        UltraMode.WriteDiagnostics(profile, entry, "DXVK / Wine game process", arguments, childEnvironment, game.Id, policy);
        GameSessions.Observe(game, entry, Path.Combine(Path.GetDirectoryName(wine)!, "wineserver"), environment["WINEPREFIX"], "DXVK", log, ultra, reservation);
        return Task.CompletedTask;
    }

}
