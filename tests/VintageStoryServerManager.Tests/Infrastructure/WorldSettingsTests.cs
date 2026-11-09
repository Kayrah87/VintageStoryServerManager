using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using VintageStoryServerManager.Core.Server;
using VintageStoryServerManager.Core.WorldSettings;
using VintageStoryServerManager.Infrastructure.Config;
using VintageStoryServerManager.Infrastructure.WorldSettings;
using VintageStoryServerManager.Tests.Support;

namespace VintageStoryServerManager.Tests.Infrastructure;

public sealed class WorldSettingsTests : IDisposable
{
    // Shaped like the definitions embedded in VSSurvivalMod.dll (lenient JSON with trailing commas).
    private const string Definitions = """
        {
            "playstyles": [
                { "code": "surviveandbuild", "langCode": "surviveandbuild-bands", "listOrder": 1,
                  "worldConfig": { "temporalStorms": "sometimes", "gameMode": "survival", "worldClimate": "realistic", } },
                { "code": "wildernesssurvival", "langCode": "wildernesssurvival", "listOrder": 2,
                  "worldConfig": { "temporalStorms": "often", "playerHealthPoints": "10", "allowCoordinateHud": "false" } },
            ],
            "worldConfigAttributes": [
                { "category": "spawnndeath", "code": "gameMode", "dataType": "dropdown", "values": ["survival", "creative"], "names": ["Survival", "Creative"], "default": "survival" },
                { "category": "temporalstability", "code": "temporalStorms", "dataType": "dropdown",
                  "values": ["off", "rare", "sometimes", "often"], "names": ["Off", "Rare", "Sometimes", "Often"], "default": "sometimes" },
                { "category": "survivalchallenges", "code": "playerHealthPoints", "dataType": "dropdown", "values": ["10", "15", "20"], "names": ["10 hp", "15 hp", "20 hp"], "default": "15" },
                { "category": "survivalchallenges", "code": "allowCoordinateHud", "dataType": "bool", "default": "true" },
                { "category": "worldgen", "code": "worldClimate", "dataType": "dropdown", "values": ["realistic", "patchy"], "names": ["Realistic", "Patchy"],
                  "default": "realistic", "onlyDuringWorldCreate": true },
            ]
        }
        """;

    private const string Language = """
        {
            "worldattribute-temporalStorms": "Temporal storms",
            "worldattribute-temporalStorms-desc": "How often <strong>temporal storms</strong> should occur",
            "worldconfig-temporalStorms-Rare": "Every 20-30 days",
            "worldconfig-category-temporalstability": "Temporal stability",
            "playstyle-wildernesssurvival": "Wilderness Survival",
            "unknown": "unknown",
            "Unknown": "Unknown",
        }
        """;

    private readonly TempDataRoot _root = new();
    private readonly FakeGameServer _server = new();
    private readonly Dictionary<string, string> _worldState = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gameMode"] = "survival",
        ["temporalStorms"] = "sometimes",
        ["playerHealthPoints"] = "15",
        ["allowCoordinateHud"] = "True",
        ["worldClimate"] = "realistic",
    };

    public WorldSettingsTests()
    {
        var mods = Path.Combine(_root.Paths.GameInstallDirectory, "Mods");
        Directory.CreateDirectory(mods);
        File.WriteAllBytes(Path.Combine(mods, "VSSurvivalMod.dll"), WrapInBinary(Definitions));
        var lang = Path.Combine(_root.Paths.GameInstallDirectory, "assets", "game", "lang");
        Directory.CreateDirectory(lang);
        File.WriteAllText(Path.Combine(lang, "en.json"), Language);

        // Answers /worldconfig like the real server.
        _server.Respond = command =>
        {
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts is ["/worldconfig", var code])
            {
                if (_silent.Contains(code))
                {
                    return [];
                }

                return _worldState.TryGetValue(code, out var current)
                    ? [$"9.11.2026 11:07:27 [Server Notification] {code} currently has value: {current}"]
                    : [$"9.11.2026 16:24:34 [Server Notification] No such config found: {code}"];
            }

            if (parts is ["/worldconfig", var key, var value])
            {
                if (!_worldState.ContainsKey(key))
                {
                    return [$"9.11.2026 16:24:38 [Server Notification] No such config found: {key}"];
                }

                _worldState[key] = value;
                return [$"9.11.2026 11:07:29 [Server Notification] Ok, value {value} set. Restart game world or server to apply changes."];
            }

            if (parts is ["/worldconfigcreate", var type, var newKey, var newValue])
            {
                _created.Add($"{type} {newKey}");
                _worldState[newKey] = newValue;
                return [$"9.11.2026 16:24:40 [Server Notification] Ok, value {newValue} set"];
            }

            return [];
        };
    }

    private readonly HashSet<string> _silent = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _created = [];

    private WorldSettingsService Service => new(
        new FakeGameInstallation(directory: _root.Paths.GameInstallDirectory), _server, _root.Options, TimeProvider.System,
        NullLogger<WorldSettingsService>.Instance)
    {
        ResponseTimeout = TimeSpan.FromMilliseconds(200),
    };

    [Fact]
    public void Extracts_definitions_from_assembly_bytes_and_builds_the_schema()
    {
        var schema = Service.GetSchema()!;

        Assert.Equal(5, schema.Settings.Count);
        var storms = schema.Find("temporalstorms")!;
        Assert.Equal("Temporal storms", storms.Label);
        Assert.Equal("How often temporal storms should occur", storms.Description);
        Assert.Equal("Every 20-30 days", storms.LabelFor("rare"));
        Assert.Equal("Temporal stability", schema.CategoryLabel("temporalstability"));
        Assert.Equal("Survival challenges", schema.CategoryLabel("survivalchallenges"));
        Assert.True(schema.Find("worldClimate")!.OnlyDuringWorldCreate);
        Assert.Equal(WorldSettingType.Bool, schema.Find("allowCoordinateHud")!.Type);
        Assert.Equal(["surviveandbuild", "wildernesssurvival"], schema.PlayStyles.Select(p => p.Code));
        Assert.Equal("Wilderness Survival", schema.PlayStyles[1].Label);
        Assert.Equal("often", schema.PlayStyles[1].WorldConfig["temporalStorms"]);
    }

    [Fact]
    public void Missing_definitions_give_no_schema() =>
        Assert.Null(WorldSettingsService.ExtractDefinitions(Encoding.UTF8.GetBytes("MZ no json here")));

    [Fact]
    public void Normalizes_and_rejects_values()
    {
        var schema = Service.GetSchema()!;

        Assert.Equal("rare", schema.Find("temporalStorms")!.Normalize("RARE"));
        Assert.Null(schema.Find("temporalStorms")!.Normalize("bogus"));
        Assert.Equal("true", schema.Find("allowCoordinateHud")!.Normalize("True"));
        Assert.Null(schema.Find("allowCoordinateHud")!.Normalize("maybe"));
        Assert.Equal("double", schema.Find("playerHealthPoints")!.CreateType);
        Assert.Equal("string", schema.Find("temporalStorms")!.CreateType);
        Assert.Equal("bool", schema.Find("allowCoordinateHud")!.CreateType);
    }

    [Fact]
    public async Task Reads_current_values_from_the_server_and_caches_them()
    {
        _server.SetStatus(ServerStatus.Running);

        var values = await Service.ReadCurrentAsync();

        Assert.Equal(5, values.Values.Count);
        Assert.Equal("sometimes", values.Values["temporalStorms"]);
        Assert.Equal(5, _server.Commands.Count);
        Assert.Equal("sometimes", Service.GetCached()!.Values["temporalStorms"]);
    }

    [Fact]
    public async Task Settings_the_world_lacks_use_the_default_and_are_added_when_changed()
    {
        // A world can be created without some settings; the game then answers "No such config found".
        _worldState.Remove("temporalStorms");
        _worldState.Remove("allowCoordinateHud");
        _server.SetStatus(ServerStatus.Running);

        var values = await Service.ReadCurrentAsync();

        Assert.True(values.IsNotSet("temporalStorms"));
        Assert.True(values.IsNotSet("allowCoordinateHud"));
        Assert.Equal("realistic", values.Values["worldClimate"]); // later settings are still read

        await Service.ApplyAsync(new Dictionary<string, string> { ["temporalStorms"] = "rare", ["allowCoordinateHud"] = "true" });

        Assert.Equal(["string temporalStorms", "bool allowCoordinateHud"], _created);
        Assert.Equal("rare", _worldState["temporalStorms"]);
        var cached = Service.GetCached()!;
        Assert.False(cached.IsNotSet("temporalStorms"));
        Assert.Equal("true", cached.Values["allowCoordinateHud"]);
    }

    [Fact]
    public async Task One_unanswered_setting_does_not_stop_the_rest_being_read()
    {
        _silent.Add("temporalStorms");
        _server.SetStatus(ServerStatus.Running);

        var values = await Service.ReadCurrentAsync();

        Assert.False(values.Values.ContainsKey("temporalStorms"));
        Assert.False(values.IsNotSet("temporalStorms"));
        Assert.Equal(4, values.Values.Count);
    }

    [Fact]
    public async Task Waits_for_each_reply_before_sending_the_next_command()
    {
        // A busy game server can read several queued commands as one, so a read of setting A
        // followed by a read of B would set A to "/worldconfig B".
        _server.SetStatus(ServerStatus.Running);
        _server.ReplyDelay = TimeSpan.FromMilliseconds(30);

        var values = await Service.ReadCurrentAsync();
        await Service.ApplyAsync(new Dictionary<string, string> { ["temporalStorms"] = "rare", ["allowCoordinateHud"] = "false" });

        Assert.Equal(5, values.Values.Count);
        Assert.Equal(7, _server.Commands.Count);
        Assert.Equal(0, _server.CommandsSentBeforeReply);
    }

    [Fact]
    public async Task Applies_valid_changes_and_refuses_invalid_ones()
    {
        _server.SetStatus(ServerStatus.Running);
        var service = Service;

        await service.ApplyAsync(new Dictionary<string, string> { ["temporalStorms"] = "Rare", ["allowCoordinateHud"] = "false" });

        Assert.Equal("rare", _worldState["temporalStorms"]);
        Assert.Contains("/worldconfig allowCoordinateHud false", _server.Commands);
        Assert.Equal("rare", service.GetCached()!.Values["temporalStorms"]);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyAsync(new Dictionary<string, string> { ["temporalStorms"] = "bogus" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyAsync(new Dictionary<string, string> { ["worldClimate"] = "patchy" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyAsync(new Dictionary<string, string> { ["noSuchSetting"] = "x" }));
        Assert.Equal("realistic", _worldState["worldClimate"]);
    }

    [Fact]
    public async Task Needs_a_running_server()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ReadCurrentAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ApplyAsync(new Dictionary<string, string> { ["temporalStorms"] = "rare" }));
    }

    [Fact]
    public async Task Save_and_restart_applies_world_changes_then_rewrites_server_settings_while_stopped()
    {
        var ctx = new ModTestContext();
        try
        {
            File.WriteAllText(ctx.Paths.ServerConfigPath, """{ "ServerName": "Old", "MaxClients": 16, "AllowPvP": true, "Roles": [] }""");
            ctx.Server.SetStatus(ServerStatus.Running);
            ctx.Server.Respond = _server.Respond;
            var statuses = new List<ServerStatus>();
            ctx.Server.StatusChanged += s => statuses.Add(s.Status);

            var world = new WorldSettingsService(new FakeGameInstallation(directory: _root.Paths.GameInstallDirectory), ctx.Server, ctx.Root.Options,
                TimeProvider.System, NullLogger<WorldSettingsService>.Instance);
            var config = new ServerConfigService(ctx.Root.Options, ctx.Server, ctx.Backups, ctx.Lock, NullLogger<ServerConfigService>.Instance);
            var applier = new SettingsApplier(world, config, ctx.Server, NullLogger<SettingsApplier>.Instance);

            await applier.ApplyAsync(
                new Dictionary<string, string> { ["gameMode"] = "creative" },
                new Dictionary<string, JsonNode?> { ["ServerName"] = "New name", ["MaxClients"] = 32, ["AllowPvP"] = false, ["Password"] = null },
                restart: true);

            Assert.Equal("creative", _worldState["gameMode"]);
            Assert.Equal([ServerStatus.Stopped, ServerStatus.Running], statuses);
            var saved = JsonNode.Parse(File.ReadAllText(ctx.Paths.ServerConfigPath))!.AsObject();
            Assert.Equal("New name", saved["ServerName"]!.GetValue<string>());
            Assert.Equal(32, saved["MaxClients"]!.GetValue<int>());
            Assert.False(saved["AllowPvP"]!.GetValue<bool>());
            Assert.True(saved.ContainsKey("Password") && saved["Password"] is null);
            Assert.NotNull(saved["Roles"]); // untouched settings are kept
        }
        finally
        {
            ctx.Dispose();
        }
    }

    [Fact]
    public async Task World_changes_need_the_server_running()
    {
        var ctx = new ModTestContext();
        try
        {
            var world = new WorldSettingsService(new FakeGameInstallation(directory: _root.Paths.GameInstallDirectory), ctx.Server, ctx.Root.Options,
                TimeProvider.System, NullLogger<WorldSettingsService>.Instance);
            var config = new ServerConfigService(ctx.Root.Options, ctx.Server, ctx.Backups, ctx.Lock, NullLogger<ServerConfigService>.Instance);
            var applier = new SettingsApplier(world, config, ctx.Server, NullLogger<SettingsApplier>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                applier.ApplyAsync(new Dictionary<string, string> { ["gameMode"] = "creative" }, new Dictionary<string, JsonNode?>(), restart: true));
        }
        finally
        {
            ctx.Dispose();
        }
    }

    [Fact]
    public void Parses_console_replies()
    {
        Assert.True(WorldSettingsParser.TryParseCurrentValue("9.11.2026 11:07:27 [Server Notification] temporalStorms currently has value: sometimes", out var code, out var value));
        Assert.Equal(("temporalStorms", "sometimes"), (code, value));
        Assert.True(WorldSettingsParser.IsValueSetConfirmation("9.11.2026 11:07:29 [Server Notification] Ok, value rare set. Restart game world or server to apply changes."));
        Assert.False(WorldSettingsParser.IsValueSetConfirmation("[Server Chat] Ok, value rare set"));
        Assert.False(WorldSettingsParser.IsValueSetConfirmation("9.11.2026 11:07:29 [Server Chat] 0 | kay: [Server Notification] Ok, value rare set"));
        Assert.True(WorldSettingsParser.IsValueSetConfirmation("9.11.2026 16:24:40 [Server Notification] Ok, value True set"));
        Assert.True(WorldSettingsParser.TryParseNotSet("9.11.2026 16:24:34 [Server Notification] No such config found: allowLandClaiming", out var missing));
        Assert.Equal("allowLandClaiming", missing);
        Assert.False(WorldSettingsParser.TryParseNotSet("9.11.2026 16:24:34 [Server Chat] 0 | kay: No such config found: x", out _));
        Assert.True(WorldSettingsParser.IsWorldSizeRefusal("9.11.2026 16:29:36 [Server Notification] Changing world size is not supported"));
    }

    /// <summary>Surrounds the JSON with binary noise and stray braces, as in a real assembly.</summary>
    private static byte[] WrapInBinary(string json) =>
        [.. new byte[] { 0x4D, 0x5A, 0x90, 0x00, (byte)'{', 0x01, (byte)'}', 0xFF }, .. Encoding.UTF8.GetBytes(json), .. new byte[] { 0x00, (byte)'{', 0x13, 0x37 }];

    public void Dispose() => _root.Dispose();
}
