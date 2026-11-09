namespace VintageStoryServerManager.Core.Server;

/// <summary>The Vintage Story server process, supervised by the manager.</summary>
public interface IGameServer
{
    ServerSnapshot Snapshot { get; }

    /// <summary>Raised when <see cref="Snapshot"/> changes.</summary>
    event Action<ServerSnapshot>? StatusChanged;

    /// <summary>Raised for every console line (output, input echo and manager notes).</summary>
    event Action<ConsoleLine>? ConsoleOutput;

    IReadOnlyList<ConsoleLine> ConsoleHistory();

    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends <c>/stop</c> and waits for a clean exit; kills the process after the timeout.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    Task RestartAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes a command to the server console. A leading slash is added if missing.</summary>
    Task SendCommandAsync(string command, CancellationToken cancellationToken = default);
}
