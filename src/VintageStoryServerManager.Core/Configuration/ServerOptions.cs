namespace VintageStoryServerManager.Core.Configuration;

/// <summary>How the manager runs the Vintage Story server process.</summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>Start the game server when the container starts.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Start the game server again if it exits without being asked to.</summary>
    public bool RestartOnCrash { get; set; } = true;

    /// <summary>How long to wait for a clean shutdown after <c>/stop</c> before killing the process.</summary>
    public int StopTimeoutSeconds { get; set; } = 90;

    /// <summary>Console lines kept in memory for the web console.</summary>
    public int ConsoleHistoryLines { get; set; } = 2000;
}
