using System.Text.Json.Nodes;
using VintageStoryServerManager.Core.Configuration;

namespace VintageStoryServerManager.Tests.Core;

public sealed class ImportedDataPathsTests
{
    private const string DataDirectory = "/data/vintagestory";
    private const string GameDirectory = "/opt/vintagestory";

    private static JsonObject Config(string save, params string[] modPaths) => new()
    {
        ["ServerName"] = "Test",
        ["ModPaths"] = new JsonArray([.. modPaths.Select(p => JsonValue.Create(p))]),
        ["WorldConfig"] = new JsonObject { ["SaveFileLocation"] = save, ["WorldName"] = "/vintagestory/data is not a path" },
    };

    [Fact]
    public void Moves_paths_from_another_data_folder_when_the_world_is_here()
    {
        var config = Config("/vintagestory/data/Saves/default.vcdbs", "Mods", "/vintagestory/data/Mods", "/opt/extra-mods");
        var existing = new HashSet<string> { "/data/vintagestory/Saves/default.vcdbs" };

        var changes = ImportedDataPaths.Fix(config, DataDirectory, GameDirectory, existing.Contains);

        Assert.Equal(2, changes.Count);
        Assert.Equal("/data/vintagestory/Saves/default.vcdbs", (string?)config["WorldConfig"]!["SaveFileLocation"]);
        Assert.Equal(["Mods", "/data/vintagestory/Mods", "/opt/extra-mods"], config["ModPaths"]!.AsArray().Select(p => (string?)p));
        Assert.Equal("/vintagestory/data is not a path", (string?)config["WorldConfig"]!["WorldName"]);
    }

    [Theory]
    [InlineData("/data/vintagestory/Saves/default.vcdbs")] // already this folder
    [InlineData("/vintagestory/data/Saves/missing.vcdbs")] // world not found here either
    [InlineData("./data/Saves/missing.vcdbs")]             // relative, world not found
    public void Leaves_other_configs_alone(string save)
    {
        var config = Config(save, "/vintagestory/data/Mods");
        var existing = new HashSet<string> { "/data/vintagestory/Saves/default.vcdbs" };

        Assert.Empty(ImportedDataPaths.Fix(config, DataDirectory, GameDirectory, existing.Contains));
        Assert.Equal(save, (string?)config["WorldConfig"]!["SaveFileLocation"]);
    }

    [Fact]
    public void Moves_relative_paths_from_a_server_started_with_a_relative_data_path()
    {
        // zsuatem/vintagestory runs the game with --dataPath ./data from /vintagestory.
        var config = Config("./data/Saves/default.vcdbs", "Mods", "./data/Mods");
        var existing = new HashSet<string> { "/data/vintagestory/Saves/default.vcdbs" };

        var changes = ImportedDataPaths.Fix(config, DataDirectory, GameDirectory, existing.Contains);

        Assert.Equal(2, changes.Count);
        Assert.Equal("/data/vintagestory/Saves/default.vcdbs", (string?)config["WorldConfig"]!["SaveFileLocation"]);
        Assert.Equal(["Mods", "/data/vintagestory/Mods"], config["ModPaths"]!.AsArray().Select(p => (string?)p));
    }

    [Fact]
    public void Moves_a_world_path_that_starts_at_saves_but_keeps_relative_mod_paths()
    {
        var config = Config("Saves/default.vcdbs", "Mods");
        var existing = new HashSet<string> { "/data/vintagestory/Saves/default.vcdbs" };

        Assert.Single(ImportedDataPaths.Fix(config, DataDirectory, GameDirectory, existing.Contains));
        Assert.Equal("/data/vintagestory/Saves/default.vcdbs", (string?)config["WorldConfig"]!["SaveFileLocation"]);
        Assert.Equal(["Mods"], config["ModPaths"]!.AsArray().Select(p => (string?)p));
    }

    [Fact]
    public void Leaves_the_config_alone_when_the_recorded_world_still_exists()
    {
        var config = Config("/vintagestory/data/Saves/default.vcdbs");
        var existing = new HashSet<string> { "/vintagestory/data/Saves/default.vcdbs", "/data/vintagestory/Saves/default.vcdbs" };

        Assert.Empty(ImportedDataPaths.Fix(config, DataDirectory, GameDirectory, existing.Contains));
    }
}
