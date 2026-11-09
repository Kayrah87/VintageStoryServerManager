using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using VintageStoryServerManager.Core.Configuration;
using VintageStoryServerManager.Core.Players;
using VintageStoryServerManager.Core.Server;

namespace VintageStoryServerManager.Infrastructure.Players;

public enum BanUnit
{
    Hour,
    Day,
    Week,
    Year,
}

/// <summary>
/// Player lists from the server's Playerdata files, and player actions sent as console
/// commands (the server keeps those files in memory, so it must be running to change them).
/// </summary>
public sealed partial class PlayerService(IOptions<DataPathOptions> paths, IGameServer server)
{
    public PlayerLists Read()
    {
        var dir = paths.Value.PlayerDataDirectory;
        var known = ReadArray(Path.Combine(dir, "playerdata.json"))
            .Select(p => new PlayerRecord(
                Str(p, "LastKnownPlayername") ?? Str(p, "PlayerUID") ?? "?",
                Str(p, "PlayerUID"),
                Str(p, "RoleCode"),
                null, null, null))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var whitelisted = ReadArray(Path.Combine(dir, "playerswhitelisted.json")).Select(Entry).ToList();
        var banned = ReadArray(Path.Combine(dir, "playersbanned.json")).Select(Entry).ToList();

        var roles = new List<string>();
        string? defaultRole = null;
        int? whitelistMode = null;
        try
        {
            if (File.Exists(paths.Value.ServerConfigPath) &&
                JsonNode.Parse(File.ReadAllText(paths.Value.ServerConfigPath)) is JsonObject config)
            {
                if (config["Roles"] is JsonArray roleArray)
                {
                    roles.AddRange(roleArray.OfType<JsonObject>().Select(r => Str(r, "Code")).OfType<string>());
                }

                defaultRole = Str(config, "DefaultRoleCode");
                whitelistMode = config["WhitelistMode"] is JsonValue mode && mode.TryGetValue(out int m) ? m : null;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // The page still shows the player lists without role information.
        }

        return new PlayerLists(known, whitelisted, banned, roles, defaultRole, whitelistMode);
    }

    public Task KickAsync(string player, string? reason) =>
        server.SendCommandAsync($"/kick {Name(player)} {Reason(reason, "Kicked by an admin")}");

    public Task BanAsync(string player, int amount, BanUnit unit, string? reason) =>
        server.SendCommandAsync($"/ban {Name(player)} {Math.Max(1, amount)} {unit.ToString().ToLowerInvariant()} {Reason(reason, "Banned by an admin")}");

    public Task UnbanAsync(string player) => server.SendCommandAsync($"/unban {Name(player)}");

    public Task SetWhitelistedAsync(string player, bool whitelisted) =>
        server.SendCommandAsync($"/player {Name(player)} whitelist {(whitelisted ? "on" : "off")}");

    public Task SetRoleAsync(string player, string role) =>
        server.SendCommandAsync($"/player {Name(player)} role {Name(role)}");

    /// <summary>Vintage Story player names and role codes: letters, digits, underscores, dashes and dots.</summary>
    private static string Name(string value)
    {
        var trimmed = value.Trim();
        if (!PlayerName().IsMatch(trimmed))
        {
            throw new ArgumentException($"'{value}' is not a valid player name or role.");
        }

        return trimmed;
    }

    private static string Reason(string? reason, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim();
        return text.Replace('\n', ' ').Replace('\r', ' ');
    }

    private static PlayerRecord Entry(JsonObject o) => new(
        Str(o, "PlayerName") ?? Str(o, "PlayerUID") ?? "?",
        Str(o, "PlayerUID"),
        null,
        Str(o, "UntilDate"),
        Str(o, "Reason"),
        Str(o, "IssuedByPlayerName"));

    private static IEnumerable<JsonObject> ReadArray(string path)
    {
        try
        {
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonArray array
                ? array.OfType<JsonObject>().ToList()
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    private static string? Str(JsonObject o, string key) =>
        o.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value is JsonValue v &&
        v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,64}$")]
    private static partial Regex PlayerName();
}
