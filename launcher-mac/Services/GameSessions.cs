using System.Diagnostics;

namespace DustoreLauncherV.Mac.Services;

internal sealed class GameSessionEventArgs(Guid id, string route, int? exitCode = null, string? error = null) : EventArgs
{
    public Guid Id { get; } = id;
    public string Route { get; } = route;
    public int? ExitCode { get; } = exitCode;
    public string? Error { get; } = error;
}

/// <summary>Records actual launcher-started Wine children and their exact runtime/prefix route.</summary>
internal static class GameSessions
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Guid, WineSession> Active = new();
    private static readonly Dictionary<Guid, string> Pending = new();
    public static event EventHandler<GameSessionEventArgs>? Changed;
    public static bool HasWineSessions { get { lock (Gate) return Active.Count > 0; } }
    public static WineSession? Find(Guid id) { lock (Gate) return Active.GetValueOrDefault(id); }
    internal static WineSession[] Snapshot() { lock (Gate) return Active.Values.ToArray(); }

    public static Reservation Reserve(Guid id, string prefix)
    {
        lock (Gate)
        {
            if (Active.ContainsKey(id) || Pending.ContainsKey(id)
                || Active.Values.Any(s => s.Prefix.Equals(prefix, StringComparison.Ordinal))
                || Pending.Values.Contains(prefix, StringComparer.Ordinal))
                throw new InvalidOperationException("Игра в этом окружении Wine уже запущена. Сначала закройте её.");
            Pending.Add(id, prefix);
            return new Reservation(id, prefix);
        }
    }

    public static void Observe(Process process, GameEntry entry, string server, string prefix, string route, string log, bool ultra, Reservation reservation)
    {
        var session = new WineSession(process, entry, server, prefix, route, log, ultra);
        lock (Gate)
        {
            if (reservation.Id != entry.Id || reservation.Prefix != prefix || reservation.Released || !Pending.ContainsKey(entry.Id))
                throw new InvalidOperationException("Резерв запуска игры уже освобождён.");
            Active.Add(entry.Id, session);
            Pending.Remove(entry.Id);
            reservation.Released = true;
        }
        Changed?.Invoke(null, new(entry.Id, route));
        _ = CompleteAsync(session);
    }

    internal sealed class Reservation(Guid id, string prefix) : IDisposable
    {
        internal Guid Id { get; } = id;
        internal string Prefix { get; } = prefix;
        internal bool Released { get; set; }
        public void Dispose()
        {
            lock (Gate)
            {
                if (Released) return;
                Pending.Remove(Id);
                Released = true;
            }
        }
    }

    private static async Task CompleteAsync(WineSession session)
    {
        string? error = null; int? code = null;
        try { code = await session.ObserveAsync().ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { error = "Не удалось наблюдать сессию игры: " + e.Message; }
        finally
        {
            lock (Gate) Active.Remove(session.Entry.Id);
            session.Dispose();
            if (code is not null and not 0 && !session.StopRequested)
                error = $"{session.Entry.Name}: Wine завершился с кодом {code}. Журнал: {session.Log}";
            Changed?.Invoke(null, new(session.Entry.Id, session.Route, code, error));
        }
    }

    internal sealed class WineSession(Process process, GameEntry entry, string server, string prefix, string route, string log, bool ultra) : IDisposable
    {
        public GameEntry Entry { get; } = entry;
        public string Prefix { get; } = prefix;
        public string Route { get; } = route;
        public string Log { get; } = log;
        public bool StopRequested { get; private set; }
        private Process? _assertion;

        public Task StopAsync(CancellationToken ct) => StopAsync(ct, allowProcessKill: true);

        internal async Task StopAsync(CancellationToken ct, bool allowProcessKill)
        {
            StopRequested = true;
            if (File.Exists(server))
            {
                var (code, text) = await WineRuntime.RunAsync(server, ["-k"], new Dictionary<string, string> { ["WINEPREFIX"] = Prefix }, TimeSpan.FromSeconds(20), ct);
                if (code != 0) throw new IOException("Wine не подтвердил запрос закрытия: " + text.Trim());
            }
            else if (!process.HasExited)
            {
                if (!allowProcessKill)
                    throw new IOException("Wine не может подтвердить остановку игры. DUSTORE остаётся открытым; закройте игру через её панель.");
                process.Kill(entireProcessTree: false);
            }
        }

        public async Task<int> ObserveAsync()
        {
            if (ultra) _assertion = AssertionFor(process.Id);
            Task stdout = DrainBoundedAsync(process.StandardOutput.BaseStream, Log + ".stdout");
            Task stderr = DrainBoundedAsync(process.StandardError.BaseStream, Log);
            await process.WaitForExitAsync().ConfigureAwait(false);
            int exit = process.ExitCode;
            // A bootstrap executable may leave another game process in this same prefix.
            // wineserver -w waits for that instance only, without periodic process scans.
            if (File.Exists(server))
            {
                var start = new ProcessStartInfo(server) { UseShellExecute = false };
                start.ArgumentList.Add("-w"); start.Environment["WINEPREFIX"] = Prefix;
                using var wait = Process.Start(start);
                if (wait is not null)
                {
                    ReleaseAssertion(); if (ultra) _assertion = AssertionFor(wait.Id);
                    await wait.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return exit;
        }

        private static Process? AssertionFor(int id)
        {
            try
            {
                var start = new ProcessStartInfo("/usr/bin/caffeinate") { UseShellExecute = false };
                foreach (string arg in new[] { "-i", "-w", id.ToString() }) start.ArgumentList.Add(arg);
                return Process.Start(start);
            }
            catch (System.ComponentModel.Win32Exception) { return null; }
        }

        private static async Task DrainBoundedAsync(Stream input, string path)
        {
            Stream output;
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 16384, useAsync: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { output = Stream.Null; }
            await using (output)
            {
                byte[] buffer = new byte[16384]; long written = 0;
                for (int count; (count = await input.ReadAsync(buffer).ConfigureAwait(false)) > 0;)
                {
                    int keep = (int)Math.Min(count, Math.Max(0, 2 * 1024 * 1024 - written));
                    if (keep > 0) { await output.WriteAsync(buffer.AsMemory(0, keep)).ConfigureAwait(false); written += keep; }
                    // Continue draining after the limit so a noisy child never blocks on a full pipe.
                }
            }
        }
        private void ReleaseAssertion()
        {
            if (_assertion is null) return;
            try { if (!_assertion.HasExited) _assertion.Kill(entireProcessTree: false); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _assertion.Dispose(); _assertion = null;
        }
        public void Dispose() { ReleaseAssertion(); process.Dispose(); }
    }
}
