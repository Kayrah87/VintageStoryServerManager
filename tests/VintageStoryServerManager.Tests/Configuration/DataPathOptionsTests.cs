using VintageStoryServerManager.Core.Configuration;

namespace VintageStoryServerManager.Tests.Configuration;

public sealed class DataPathOptionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vssm-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Defaults_match_container_layout()
    {
        var options = new DataPathOptions();

        Assert.Equal("/data", options.Root);
        Assert.Equal("/opt/vintagestory", options.GameInstallDirectory);
        Assert.Equal("/data/vintagestory", options.GameDataDirectory);
        Assert.Equal("/data/vintagestory/Mods", options.ModsDirectory);
        Assert.Equal("/data/manager/manager.db", options.DatabasePath);
        Assert.Equal("/data/backups", options.BackupsDirectory);
    }

    [Fact]
    public void Relative_root_resolves_to_absolute_path()
    {
        var options = new DataPathOptions { Root = "relative/data" };

        Assert.True(Path.IsPathRooted(options.Root));
        Assert.EndsWith(Path.Combine("relative", "data"), options.Root);
    }

    [Fact]
    public void EnsureDirectories_creates_full_layout()
    {
        var options = new DataPathOptions { Root = _root };

        options.EnsureDirectories();

        Assert.True(Directory.Exists(options.ModsDirectory));
        Assert.True(Directory.Exists(options.DataProtectionKeysDirectory));
        Assert.True(Directory.Exists(options.BackupsDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
