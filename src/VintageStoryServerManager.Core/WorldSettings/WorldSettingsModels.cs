using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace VintageStoryServerManager.Core.WorldSettings;

public enum WorldSettingType
{
    Dropdown,
    Bool,
}

public sealed record WorldSettingOption(string Value, string Label);

/// <summary>One world setting as the game defines it (e.g. temporalStorms, gameMode).</summary>
public sealed record WorldSettingDefinition(
    string Code,
    string Category,
    WorldSettingType Type,
    IReadOnlyList<WorldSettingOption> Options,
    string Default,
    bool OnlyDuringWorldCreate,
    string Label,
    string? Description)
{
    /// <summary>
    /// The canonical spelling of <paramref name="value"/> if the setting accepts it, otherwise null.
    /// The server accepts anything for /worldconfig, so values are checked here.
    /// </summary>
    public string? Normalize(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        return Type switch
        {
            WorldSettingType.Bool => bool.TryParse(trimmed, out var b) ? (b ? "true" : "false") : null,
            _ => Options.FirstOrDefault(o => string.Equals(o.Value, trimmed, StringComparison.OrdinalIgnoreCase))?.Value,
        };
    }

    /// <summary>The type <c>/worldconfigcreate</c> needs to add this setting to a world that lacks it.</summary>
    public string CreateType =>
        Type == WorldSettingType.Bool ? "bool"
        : Options.Count > 0 && Options.All(o => double.TryParse(o.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) ? "double"
        : "string";

    public string LabelFor(string? value) =>
        Type == WorldSettingType.Bool
            ? (Normalize(value) == "true" ? "On" : "Off")
            : Options.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase))?.Label ?? value ?? "";
}

/// <summary>A play style (Survive and build, Wilderness survival, …) and the world settings it uses.</summary>
public sealed record PlayStyle(string Code, string Label, IReadOnlyDictionary<string, string> WorldConfig);

public sealed record WorldSettingsSchema(
    IReadOnlyList<WorldSettingDefinition> Settings,
    IReadOnlyDictionary<string, string> CategoryLabels,
    IReadOnlyList<PlayStyle> PlayStyles)
{
    public WorldSettingDefinition? Find(string code) =>
        Settings.FirstOrDefault(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase));

    public string CategoryLabel(string category) => CategoryLabels.GetValueOrDefault(category) ?? category;
}

/// <summary>
/// World setting values read from the running server. <paramref name="NotSet"/> lists settings the
/// world doesn't store (it was created without them), so the game uses their default.
/// </summary>
public sealed record WorldSettingValues(IReadOnlyDictionary<string, string> Values, DateTimeOffset ReadAt, IReadOnlyList<string>? NotSet = null)
{
    public bool IsNotSet(string code) => NotSet?.Contains(code, StringComparer.OrdinalIgnoreCase) == true;
}

public static partial class WorldSettingsParser
{
    private static readonly Dictionary<string, string> FallbackCategoryLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gamemode"] = "Game mode",
        ["spawnndeath"] = "Player spawn and death",
        ["survivalchallenges"] = "Survival challenges",
        ["temporalstability"] = "Temporal stability",
        ["worldgen"] = "World generation",
        ["multiplayer"] = "Multiplayer",
    };

    /// <summary>
    /// Builds the schema from the survival mod's world config definitions (an object with
    /// <c>playstyles</c> and <c>worldConfigAttributes</c>) and the game's language file.
    /// </summary>
    public static WorldSettingsSchema Parse(JObject definitions, IReadOnlyDictionary<string, string>? language)
    {
        language ??= new Dictionary<string, string>();
        string? Lang(string key) => language.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? PlainText(text) : null;

        var settings = new List<WorldSettingDefinition>();
        foreach (var attribute in definitions["worldConfigAttributes"] as JArray ?? [])
        {
            if (attribute is not JObject a || a.Value<string>("code") is not { Length: > 0 } code)
            {
                continue;
            }

            var type = string.Equals(a.Value<string>("dataType"), "bool", StringComparison.OrdinalIgnoreCase)
                ? WorldSettingType.Bool
                : WorldSettingType.Dropdown;
            if (type == WorldSettingType.Dropdown && a["values"] is not JArray)
            {
                continue; // only dropdowns and switches are supported
            }

            var values = (a["values"] as JArray ?? []).Select(v => v.ToString()).ToList();
            var names = (a["names"] as JArray ?? []).Select(v => v.ToString()).ToList();
            var options = values.Select((value, i) =>
            {
                var name = i < names.Count ? names[i] : value;
                return new WorldSettingOption(value, Lang($"worldconfig-{code}-{name}") ?? name);
            }).ToList();

            settings.Add(new WorldSettingDefinition(
                code,
                a.Value<string>("category") ?? "other",
                type,
                options,
                a["default"]?.ToString() ?? (type == WorldSettingType.Bool ? "false" : values.FirstOrDefault() ?? ""),
                a.Value<bool?>("onlyDuringWorldCreate") ?? false,
                Lang($"worldattribute-{code}") ?? Humanize(code),
                Lang($"worldattribute-{code}-desc")));
        }

        var categories = settings.Select(s => s.Category).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(c => c, c => Lang($"worldconfig-category-{c}") ?? FallbackCategoryLabels.GetValueOrDefault(c) ?? Humanize(c), StringComparer.OrdinalIgnoreCase);

        var playStyles = (definitions["playstyles"] as JArray ?? [])
            .OfType<JObject>()
            .Where(p => p.Value<string>("code") is { Length: > 0 })
            .OrderBy(p => p.Value<int?>("listOrder") ?? 0)
            .Select(p =>
            {
                var code = p.Value<string>("code")!;
                var langCode = p.Value<string>("langCode") ?? code;
                var config = (p["worldConfig"] as JObject ?? [])
                    .Properties()
                    .ToDictionary(prop => prop.Name, prop => prop.Value.ToString(), StringComparer.OrdinalIgnoreCase);
                return new PlayStyle(code, Lang($"playstyle-{langCode}") ?? Lang($"playstyle-{code}") ?? Humanize(code), config);
            })
            .ToList();

        return new WorldSettingsSchema(settings, categories, playStyles);
    }

    /// <summary>
    /// Replies come from the server itself: date, time, then [Server Notification]. Anchoring on
    /// that keeps chat lines ("[Server Chat] … Ok, value x set") from passing as replies.
    /// </summary>
    private const string Notification = @"^\S+ \S+ \[Server Notification\] ";

    // 9.11.2026 11:07:27 [Server Notification] temporalStorms currently has value: sometimes
    [GeneratedRegex(Notification + @"(?<code>[A-Za-z0-9_.\-]+) currently has value: (?<value>.*)$")]
    private static partial Regex CurrentValueRegex();

    // 9.11.2026 11:07:29 [Server Notification] Ok, value rare set. Restart game world or server to apply changes.
    // 9.11.2026 16:24:40 [Server Notification] Ok, value True set          (from /worldconfigcreate)
    [GeneratedRegex(Notification + @"Ok, value (?<value>.*) set(\.|$)")]
    private static partial Regex ValueSetRegex();

    // 9.11.2026 16:24:34 [Server Notification] No such config found: allowLandClaiming
    [GeneratedRegex(Notification + @"No such config found: (?<code>\S+)\s*$")]
    private static partial Regex NotSetRegex();

    // 9.11.2026 16:29:36 [Server Notification] Changing world size is not supported
    [GeneratedRegex(Notification + @"Changing world size is not supported")]
    private static partial Regex WorldSizeRegex();

    /// <summary>
    /// The game's reply to /worldconfig worldWidth or worldLength: it neither shows nor changes
    /// the world size.
    /// </summary>
    public static bool IsWorldSizeRefusal(string line) => WorldSizeRegex().IsMatch(line);

    /// <summary>The reply to reading or changing a setting the world doesn't store.</summary>
    public static bool TryParseNotSet(string line, out string code)
    {
        var match = NotSetRegex().Match(line);
        code = match.Success ? match.Groups["code"].Value : "";
        return match.Success;
    }

    public static bool TryParseCurrentValue(string line, out string code, out string value)
    {
        var match = CurrentValueRegex().Match(line);
        code = match.Success ? match.Groups["code"].Value : "";
        value = match.Success ? match.Groups["value"].Value.Trim() : "";
        return match.Success;
    }

    public static bool IsValueSetConfirmation(string line) => ValueSetRegex().IsMatch(line);

    /// <summary>The game's texts contain markup such as &lt;font color="orange"&gt; and &lt;strong&gt;.</summary>
    public static string PlainText(string text) =>
        System.Net.WebUtility.HtmlDecode(MarkupTag().Replace(text, "")).Trim();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex MarkupTag();

    /// <summary>"playerHealthPoints" → "Player health points".</summary>
    private static string Humanize(string code)
    {
        var spaced = Regex.Replace(code, "(?<=[a-z0-9])(?=[A-Z])", " ").Replace('_', ' ');
        return spaced.Length == 0 ? code : char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
    }
}
