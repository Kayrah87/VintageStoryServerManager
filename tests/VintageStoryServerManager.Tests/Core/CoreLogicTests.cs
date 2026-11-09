using VintageStoryServerManager.Core.ModDb;
using VintageStoryServerManager.Core.Mods;
using VintageStoryServerManager.Core.Server;
using VintageStoryServerManager.Core.Versioning;

namespace VintageStoryServerManager.Tests.Core;

public sealed class ModVersionTests
{
    [Theory]
    [InlineData("1.22.7", "1.22.6", 1)]
    [InlineData("1.22.7", "1.22.7", 0)]
    [InlineData("v0.8.4", "0.8.4", 0)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("2.1", "2.1.0", 0)]
    [InlineData("1.0.0", "1.0.0-rc.2", 1)]
    [InlineData("1.0.0-rc.10", "1.0.0-rc.2", 1)]
    [InlineData("1.0.0-pre.1", "1.0.0-rc.1", -1)]
    public void Compares_versions(string left, string right, int expected) =>
        Assert.Equal(expected, Math.Sign(ModVersion.Compare(left, right)));

    [Theory]
    [InlineData("0.8.4", "0.8.4", true)]
    [InlineData("0.8.5", "0.8.4", true)]
    [InlineData("0.7.2", "0.8.4", false)]
    [InlineData("1.0.0", "*", true)]
    [InlineData("1.0.0", "", true)]
    [InlineData(null, "1.0.0", false)]
    public void Checks_minimum_version_requirements(string? installed, string required, bool expected) =>
        Assert.Equal(expected, ModVersion.Satisfies(installed, required));

    [Fact]
    public void Gives_major_minor() => Assert.Equal("1.22", ModVersion.MajorMinor("1.22.7"));
}

public sealed class ServerOutputParserTests
{
    [Fact]
    public void Reads_game_version() =>
        Assert.Equal("1.22.7", ServerOutputParser.TryParseGameVersion("9.11.2026 09:36:18 [Server Notification] Game Version: v1.22.7 (Stable)"));

    [Fact]
    public void Recognises_ready_line()
    {
        Assert.True(ServerOutputParser.TryParseReady("9.11.2026 09:36:38 [Server Event] Dedicated Server now running on Port 42420 and all ips!", out var port));
        Assert.Equal(42420, port);
        Assert.False(ServerOutputParser.TryParseReady("[Server Notification] Entering runphase WorldReady", out _));
    }

    [Theory]
    [InlineData("9.11.2026 10:00:00 [Server Event] Kay [::ffff:192.168.1.5]:51234 joins.", "Kay")]
    [InlineData("[Server Event] Some_Player [10.0.0.2]:4000 joins.", "Some_Player")]
    public void Recognises_joins(string line, string player) => Assert.Equal(player, ServerOutputParser.TryParseJoin(line));

    [Theory]
    [InlineData("9.11.2026 10:05:00 [Server Event] Player Kay left.", "Kay")]
    [InlineData("[Server Event] Kay [::ffff:192.168.1.5]:51234 disconnected.", "Kay")]
    public void Recognises_leaves(string line, string player) => Assert.Equal(player, ServerOutputParser.TryParseLeave(line));

    [Fact]
    public void Ignores_chat_that_looks_like_an_event() =>
        Assert.Null(ServerOutputParser.TryParseJoin("[Server Chat] 0 | Kay: Bob [1.2.3.4]:5 joins."));
}

public sealed class ModInfoReaderTests
{
    [Fact]
    public void Reads_standard_modinfo()
    {
        var info = ModInfoReader.Parse("""
            {
              "type": "code",
              "modid": "ExLib",
              "name": "Expanded Library",
              "authors": ["fallenstar"],
              "version": "0.8.4",
              "dependencies": { "game": "1.22.0", "other": "*" }
            }
            """);

        Assert.Equal("exlib", info.ModId);
        Assert.Equal("Expanded Library", info.Name);
        Assert.Equal("0.8.4", info.Version);
        Assert.Equal(["fallenstar"], info.Authors);
        Assert.Equal("1.22.0", info.Dependencies["game"]);
    }

    [Fact]
    public void Accepts_lenient_json_like_the_game()
    {
        var info = ModInfoReader.Parse("""
            {
              // comment
              type: 'content',
              Name: 'Better Ruins',
              Version: "1.2.3",
              Dependencies: { game: "", },
            }
            """);

        Assert.Equal("betterruins", info.ModId);
        Assert.Equal("1.2.3", info.Version);
        Assert.Equal("", info.Dependencies["game"]);
    }

    [Fact]
    public void Rejects_broken_json() =>
        Assert.Throws<InvalidDataException>(() => ModInfoReader.Parse("{ \"modid\": "));
}

public sealed class ModHealthCheckerTests
{
    private static InstalledMod Mod(string file, string id, string version, bool enabled = true, Dictionary<string, string>? deps = null, string? side = null) =>
        new(file, "/mods/" + file, ModPackageKind.Zip, enabled, 1,
            new ModInfo(id, id, version, "code", side, null, [], deps ?? new Dictionary<string, string>(), null), null);

    [Fact]
    public void Healthy_mods_have_no_issues()
    {
        var issues = ModHealthChecker.Check(
            [Mod("lib.zip", "lib", "1.0.0"), Mod("app.zip", "app", "2.0.0", deps: new() { ["lib"] = "1.0.0", ["game"] = "1.22.0" })],
            "1.22.7");

        Assert.Empty(issues);
    }

    [Fact]
    public void Flags_duplicates_on_every_copy()
    {
        var issues = ModHealthChecker.Check([Mod("exlib_0.7.2.zip", "exlib", "0.7.2"), Mod("exlib_0.8.4.zip", "exlib", "0.8.4")], "1.22.7");

        Assert.Equal(2, issues.Count(i => i.Severity == IssueSeverity.Error && i.Message.Contains("installed 2 times")));
    }

    [Fact]
    public void Flags_missing_disabled_and_outdated_dependencies()
    {
        var issues = ModHealthChecker.Check(
            [
                Mod("a.zip", "a", "1.0.0", deps: new() { ["missing"] = "1.0.0", ["off"] = "", ["exlib"] = "0.8.4" }),
                Mod("off.zip", "off", "1.0.0", enabled: false),
                Mod("exlib.zip", "exlib", "0.7.2"),
            ],
            "1.22.7");

        Assert.Contains(issues, i => i.Message.Contains("'missing' 1.0.0 or newer, which isn't installed"));
        Assert.Contains(issues, i => i.Message.Contains("'off', which is disabled"));
        Assert.Contains(issues, i => i.Message.Contains("'exlib' 0.8.4 or newer, but 0.7.2 is installed"));
    }

    [Fact]
    public void Flags_mods_needing_a_newer_game()
    {
        var issues = ModHealthChecker.Check([Mod("new.zip", "new", "1.0.0", deps: new() { ["game"] = "1.23.0" })], "1.22.7");

        var issue = Assert.Single(issues);
        Assert.Contains("game version 1.23.0", issue.Message);
    }

    [Fact]
    public void Notes_client_side_mods_and_unreadable_files()
    {
        var unreadable = new InstalledMod("bad.zip", "/mods/bad.zip", ModPackageKind.Zip, true, 1, null, "modinfo.json is not valid JSON");
        var stray = new InstalledMod("notes.txt", "/mods/notes.txt", ModPackageKind.Unknown, true, 1, null, null);

        var issues = ModHealthChecker.Check([Mod("hud.zip", "hud", "1.0.0", side: "client"), unreadable, stray], "1.22.7");

        Assert.Contains(issues, i => i.Severity == IssueSeverity.Info && i.FileName == "hud.zip");
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.FileName == "bad.zip");
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.FileName == "notes.txt");
    }
}

public sealed class ReleaseSelectorTests
{
    private static ModDbRelease Release(string version, params string[] games) =>
        new(1, "m", version, $"m_{version}.zip", $"https://x/{version}", games, null);

    [Fact]
    public void Picks_newest_release_for_exact_game_version()
    {
        var pick = ReleaseSelector.PickCompatible(
            [Release("0.7.2", "1.22.6"), Release("0.8.4", "1.22.7"), Release("0.9.0", "1.23.0")], "1.22.7");

        Assert.Equal("0.8.4", pick!.Release.ModVersion);
        Assert.True(pick.ExactGameVersion);
    }

    [Fact]
    public void Falls_back_to_same_minor_version()
    {
        var pick = ReleaseSelector.PickCompatible([Release("1.0.0", "1.22.5"), Release("2.0.0", "1.21.0")], "1.22.7");

        Assert.Equal("1.0.0", pick!.Release.ModVersion);
        Assert.False(pick.ExactGameVersion);
    }

    [Fact]
    public void Returns_null_without_a_compatible_release() =>
        Assert.Null(ReleaseSelector.PickCompatible([Release("1.0.0", "1.20.0")], "1.22.7"));
}
