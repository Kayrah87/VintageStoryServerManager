namespace VintageStoryServerManager.Core.Configuration;

public enum ServerSettingKind
{
    Text,
    Password,
    Number,
    Bool,
    Choice,
}

/// <summary>A serverconfig.json field shown as a form control.</summary>
/// <param name="Key">The property name in serverconfig.json.</param>
/// <param name="Choices">For <see cref="ServerSettingKind.Choice"/>: value → label (values are stored as numbers).</param>
public sealed record ServerSettingField(
    string Key,
    string Label,
    ServerSettingKind Kind,
    string? Help = null,
    int Min = 0,
    int Max = int.MaxValue,
    IReadOnlyList<(int Value, string Label)>? Choices = null,
    bool Nullable = false);

/// <summary>The serverconfig.json settings the settings page offers as controls.</summary>
public static class ServerSettingFields
{
    public static readonly IReadOnlyList<ServerSettingField> All =
    [
        new("ServerName", "Server name", ServerSettingKind.Text, "Shown in the server list and to players."),
        new("ServerDescription", "Description", ServerSettingKind.Text, "Shown in the public server list.", Nullable: true),
        new("WelcomeMessage", "Welcome message", ServerSettingKind.Text, "{0} is replaced with the player's name."),
        new("Password", "Password", ServerSettingKind.Password, "Leave empty for no password.", Nullable: true),
        new("MaxClients", "Max players", ServerSettingKind.Number, Min: 1, Max: 1000),
        new("Port", "Port", ServerSettingKind.Number, "Also change the container's published port if you change this.", Min: 1, Max: 65535),
        new("WhitelistMode", "Whitelist", ServerSettingKind.Choice, "Who can join.",
            Choices: [(0, "Default (on unless advertised)"), (1, "On: whitelisted players only"), (2, "Off: anyone can join")]),
        new("AdvertiseServer", "List on the public server browser", ServerSettingKind.Bool),
        new("AllowPvP", "Player vs player", ServerSettingKind.Bool),
        new("AllowFireSpread", "Fire spreads", ServerSettingKind.Bool),
        new("AllowFallingBlocks", "Falling blocks (sand, gravel)", ServerSettingKind.Bool),
        new("PassTimeWhenEmpty", "Time passes when nobody is online", ServerSettingKind.Bool),
        new("MaxChunkRadius", "View distance (chunks)", ServerSettingKind.Number, "Higher uses more memory and CPU.", Min: 4, Max: 64),
        new("KickClientsAfterAfkSeconds", "Kick idle players after (seconds)", ServerSettingKind.Number, "0 never kicks.", Min: 0, Max: 86400),
    ];
}
