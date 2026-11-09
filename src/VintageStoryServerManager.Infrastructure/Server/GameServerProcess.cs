using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VintageStoryServerManager.Core.Configuration;
using VintageStoryServerManager.Core.Game;
using VintageStoryServerManager.Core.Server;
using VintageStoryServerManager.Infrastructure.Game;

namespace VintageStoryServerManager.Infrastructure.Server;

/// <summary>
/// Runs <c>VintagestoryServer.dll</c> as a child process with redirected console streams,
/// keeps a console history, tracks status and online players, and restarts after crashes.
/// </summary>
public sealed class GameServerProcess(
    IGameInstallation installation,
    IOptions<DataPathOptions> paths,
    IOptions<ServerOptions> options,
    TimeProvider time,
    ILogger<GameServerProcess> logger) : IGameServer, IDisposable
{
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The game drops console commands that arrive back to back (even in a single write), so
    /// commands are spaced out. 50 ms was enough in testing; this leaves a margin.
    /// </summary>
    internal static readonly TimeSpan MinCommandInterval = TimeSpan.FromMilliseconds(100);
    private const int MaxCrashRestarts = 3;

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly LinkedList<ConsoleLine> _history = new();
    private readonly HashSet<string> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTimeOffset> _crashRestarts = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private DateTimeOffset _lastCommandAt = DateTimeOffset.MinValue;

    private Process? _process;
    private TaskCompletionSource? _exited;
    private bool _stopRequested;
    private ServerSnapshot _snapshot = new(ServerStatus.Stopped, null, null, null, [], null, null);

    public event Action<ServerSnapshot>? StatusChanged;

    public event Action<ConsoleLine>? ConsoleOutput;

    public ServerSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public IReadOnlyList<ConsoleLine> ConsoleHistory()
    {
        lock (_gate)
        {
            return _history.ToList();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            StartCore();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken);
            StartCore();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        var text = command.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (!text.StartsWith('/'))
        {
            text = "/" + text;
        }

        Process? process;
        lock (_gate)
        {
            process = _snapshot.IsRunning ? _process : null;
        }

        if (process is null)
        {
            throw new InvalidOperationException("The server isn't running.");
        }

        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            var wait = _lastCommandAt + MinCommandInterval - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, time, cancellationToken);
            }

            AddLine(text, ConsoleLineKind.Input);
            await process.StandardInput.WriteLineAsync(text.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            _lastCommandAt = time.GetUtcNow();
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private void StartCore()
    {
        lock (_gate)
        {
            if (_snapshot.IsRunning || _snapshot.Status == ServerStatus.Stopping)
            {
                return;
            }
        }

        var gameDirectory = installation.ActiveDirectory;
        var assembly = Path.Combine(gameDirectory, GameInstallation.ServerAssembly);
        if (!File.Exists(assembly))
        {
            var message = $"Vintage Story server not found: {assembly}";
            SetSnapshot(s => s with { Status = ServerStatus.Crashed, LastError = message });
            AddLine(message, ConsoleLineKind.Manager);
            throw new InvalidOperationException(message);
        }

        Directory.CreateDirectory(paths.Value.GameDataDirectory);

        var startInfo = new ProcessStartInfo(DotnetHost())
        {
            WorkingDirectory = gameDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(assembly);
        startInfo.ArgumentList.Add("--dataPath");
        startInfo.ArgumentList.Add(paths.Value.GameDataDirectory);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) OnOutput(e.Data, ConsoleLineKind.Output); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) OnOutput(e.Data, ConsoleLineKind.Error); };

        AddLine($"Starting Vintage Story server ({installation.ActiveVersion ?? "unknown version"})…", ConsoleLineKind.Manager);
        process.Start();
        process.StandardInput.AutoFlush = true;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _process = process;
            _exited = exited;
            _stopRequested = false;
            _players.Clear();
        }

        SetSnapshot(s => new ServerSnapshot(ServerStatus.Starting, time.GetUtcNow(), installation.ActiveVersion, null, [], s.LastExitCode, null));
        logger.LogInformation("Started Vintage Story server, pid {Pid}", process.Id);
        _ = WatchExitAsync(process, exited);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Process? process;
        Task? exited;
        lock (_gate)
        {
            process = _process;
            exited = _exited?.Task;
            if (process is null || exited is null || exited.IsCompleted)
            {
                return;
            }

            _stopRequested = true;
        }

        SetSnapshot(s => s with { Status = ServerStatus.Stopping });
        AddLine("Stopping server…", ConsoleLineKind.Manager);

        try
        {
            await process.StandardInput.WriteLineAsync("/stop");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not send /stop to the server");
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(5, options.Value.StopTimeoutSeconds));
        try
        {
            await exited.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            AddLine($"Server didn't stop within {timeout.TotalSeconds:0}s; killing it.", ConsoleLineKind.Manager);
            logger.LogWarning("Vintage Story server did not stop in time; killing it");
            process.Kill(entireProcessTree: true);
            await exited.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
        }
    }

    private async Task WatchExitAsync(Process process, TaskCompletionSource exited)
    {
        await process.WaitForExitAsync();
        // Let the asynchronous output readers drain before reporting the exit.
        process.WaitForExit();

        bool requested;
        lock (_gate)
        {
            requested = _stopRequested;
            _process = null;
            _players.Clear();
        }

        var code = process.ExitCode;
        process.Dispose();

        var crashed = !requested;
        SetSnapshot(s => s with
        {
            Status = crashed ? ServerStatus.Crashed : ServerStatus.Stopped,
            StartedAt = null,
            OnlinePlayers = [],
            LastExitCode = code,
            LastError = crashed ? $"The server exited unexpectedly (code {code})." : null,
        });
        AddLine(crashed ? $"Server exited unexpectedly with code {code}." : "Server stopped.", ConsoleLineKind.Manager);
        exited.TrySetResult();

        if (crashed)
        {
            logger.LogWarning("Vintage Story server exited unexpectedly with code {Code}", code);
            await MaybeRestartAfterCrashAsync();
        }
    }

    private async Task MaybeRestartAfterCrashAsync()
    {
        if (!options.Value.RestartOnCrash)
        {
            return;
        }

        var now = time.GetUtcNow();
        lock (_gate)
        {
            while (_crashRestarts.Count > 0 && now - _crashRestarts.Peek() > CrashWindow)
            {
                _crashRestarts.Dequeue();
            }

            if (_crashRestarts.Count >= MaxCrashRestarts)
            {
                AddLine($"Not restarting: the server crashed {MaxCrashRestarts} times in {CrashWindow.TotalMinutes:0} minutes. Check the logs.", ConsoleLineKind.Manager);
                return;
            }

            _crashRestarts.Enqueue(now);
        }

        AddLine("Restarting in 10 seconds…", ConsoleLineKind.Manager);
        await Task.Delay(TimeSpan.FromSeconds(10), time);
        try
        {
            if (Snapshot.Status == ServerStatus.Crashed)
            {
                await StartAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restart after crash failed");
        }
    }

    private void OnOutput(string text, ConsoleLineKind kind)
    {
        AddLine(text, kind);

        if (ServerOutputParser.TryParseGameVersion(text) is { } version)
        {
            SetSnapshot(s => s with { GameVersion = version });
        }
        else if (ServerOutputParser.TryParseReady(text, out var port))
        {
            SetSnapshot(s => s with { Status = ServerStatus.Running, Port = port });
        }
        else if (ServerOutputParser.TryParseJoin(text) is { } joined)
        {
            UpdatePlayers(players => players.Add(joined));
        }
        else if (ServerOutputParser.TryParseLeave(text) is { } left)
        {
            UpdatePlayers(players => players.Remove(left));
        }
    }

    private void UpdatePlayers(Func<HashSet<string>, bool> change)
    {
        string[] players;
        lock (_gate)
        {
            if (!change(_players))
            {
                return;
            }

            players = _players.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        SetSnapshot(s => s with { OnlinePlayers = players });
    }

    private void AddLine(string text, ConsoleLineKind kind)
    {
        var line = new ConsoleLine(time.GetUtcNow(), text, kind);
        lock (_gate)
        {
            _history.AddLast(line);
            while (_history.Count > Math.Max(100, options.Value.ConsoleHistoryLines))
            {
                _history.RemoveFirst();
            }
        }

        ConsoleOutput?.Invoke(line);
    }

    private void SetSnapshot(Func<ServerSnapshot, ServerSnapshot> change)
    {
        ServerSnapshot updated;
        lock (_gate)
        {
            updated = _snapshot = change(_snapshot);
        }

        StatusChanged?.Invoke(updated);
    }

    /// <summary>The dotnet host running this app, so the game uses the same runtime.</summary>
    private static string DotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath)
        {
            return hostPath;
        }

        var processPath = Environment.ProcessPath;
        return processPath is not null && Path.GetFileNameWithoutExtension(processPath) == "dotnet" ? processPath : "dotnet";
    }

    public void Dispose()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
        }

        if (process is { HasExited: false })
        {
            process.Kill(entireProcessTree: true);
        }

        _lifecycle.Dispose();
        _commandGate.Dispose();
    }
}
