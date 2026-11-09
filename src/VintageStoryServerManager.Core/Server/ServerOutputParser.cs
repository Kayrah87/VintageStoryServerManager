using System.Text.RegularExpressions;

namespace VintageStoryServerManager.Core.Server;

/// <summary>Recognises the console lines the manager reacts to.</summary>
public static partial class ServerOutputParser
{
    // 9.11.2026 09:36:18 [Server Notification] Game Version: v1.22.7 (Stable)
    [GeneratedRegex(@"Game Version: v?(?<version>\d+(\.\d+)+(-[\w.]+)?)")]
    private static partial Regex GameVersionRegex();

    // [Server Event] Dedicated Server now running on Port 42420 and all ips!
    [GeneratedRegex(@"Dedicated Server now running on Port (?<port>\d+)")]
    private static partial Regex ReadyRegex();

    // [Server Event] Kay [::ffff:192.168.1.5]:51234 joins.
    [GeneratedRegex(@"\[Server Event\] (?<name>[^\s\[]+) \[[^\]]*\]:\d+ joins\.")]
    private static partial Regex JoinRegex();

    // [Server Event] Player Kay left.   /   [Server Event] Kay left.
    [GeneratedRegex(@"\[Server Event\] (Player )?(?<name>[^\s\[]+)( \[[^\]]*\]:\d+)? (left|disconnected)")]
    private static partial Regex LeaveRegex();

    public static string? TryParseGameVersion(string line)
    {
        var match = GameVersionRegex().Match(line);
        return match.Success ? match.Groups["version"].Value : null;
    }

    public static bool TryParseReady(string line, out int port)
    {
        var match = ReadyRegex().Match(line);
        port = match.Success ? int.Parse(match.Groups["port"].Value) : 0;
        return match.Success;
    }

    public static string? TryParseJoin(string line)
    {
        var match = JoinRegex().Match(line);
        return match.Success ? match.Groups["name"].Value : null;
    }

    public static string? TryParseLeave(string line)
    {
        var match = LeaveRegex().Match(line);
        return match.Success ? match.Groups["name"].Value : null;
    }
}
