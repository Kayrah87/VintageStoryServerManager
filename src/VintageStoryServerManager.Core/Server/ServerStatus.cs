namespace VintageStoryServerManager.Core.Server;

public enum ServerStatus
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Crashed,
}

public enum ConsoleLineKind
{
    Output,
    Error,
    Input,
    Manager,
}

public sealed record ConsoleLine(DateTimeOffset Time, string Text, ConsoleLineKind Kind);

/// <summary>Point-in-time view of the game server.</summary>
public sealed record ServerSnapshot(
    ServerStatus Status,
    DateTimeOffset? StartedAt,
    string? GameVersion,
    int? Port,
    IReadOnlyList<string> OnlinePlayers,
    int? LastExitCode,
    string? LastError)
{
    public bool IsRunning => Status is ServerStatus.Running or ServerStatus.Starting;

    public TimeSpan? Uptime => StartedAt is { } started && IsRunning ? DateTimeOffset.UtcNow - started : null;
}
