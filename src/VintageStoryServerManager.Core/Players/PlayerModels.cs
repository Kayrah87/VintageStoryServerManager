namespace VintageStoryServerManager.Core.Players;

/// <summary>An entry from the server's Playerdata files.</summary>
public sealed record PlayerRecord(
    string Name,
    string? Uid,
    string? Role,
    string? Until,
    string? Reason,
    string? IssuedBy);

public sealed record PlayerLists(
    IReadOnlyList<PlayerRecord> Known,
    IReadOnlyList<PlayerRecord> Whitelisted,
    IReadOnlyList<PlayerRecord> Banned,
    IReadOnlyList<string> Roles,
    string? DefaultRole,
    int? WhitelistMode);
