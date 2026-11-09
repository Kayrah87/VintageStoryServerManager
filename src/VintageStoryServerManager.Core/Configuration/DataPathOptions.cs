namespace VintageStoryServerManager.Core.Configuration;

/// <summary>
/// Where the manager and the Vintage Story server keep their files.
/// </summary>
/// <remarks>
/// Everything that must survive a container rebuild, or move to another container,
/// lives under <see cref="Root"/>, which is mounted into the container as a volume:
/// <code>
/// /data
/// ├── vintagestory/   Vintage Story data path (--dataPath): Saves, Mods, ModConfig, Logs, serverconfig.json
/// ├── manager/        manager.db (accounts), data-protection keys, disabled mods, temp files
/// ├── game/           server versions downloaded by the manager (one folder per version)
/// └── backups/        world and mod backups made by the manager
/// </code>
/// The image also bundles one server version at <see cref="GameInstallDirectory"/>.
/// </remarks>
public sealed class DataPathOptions
{
    public const string SectionName = "DataPaths";

    private string _root = "/data";
    private string _gameInstallDirectory = "/opt/vintagestory";

    /// <summary>Root of the mounted data volume. Relative paths resolve against the working directory.</summary>
    public string Root
    {
        get => _root;
        set => _root = Path.GetFullPath(value);
    }

    /// <summary>Directory holding the Vintage Story server installation (VintagestoryServer.dll).</summary>
    public string GameInstallDirectory
    {
        get => _gameInstallDirectory;
        set => _gameInstallDirectory = Path.GetFullPath(value);
    }

    /// <summary>Vintage Story data path, passed to the server as --dataPath.</summary>
    public string GameDataDirectory => Path.Combine(Root, "vintagestory");

    public string ModsDirectory => Path.Combine(GameDataDirectory, "Mods");

    public string ManagerDirectory => Path.Combine(Root, "manager");

    public string BackupsDirectory => Path.Combine(Root, "backups");

    public string DatabasePath => Path.Combine(ManagerDirectory, "manager.db");

    public string DataProtectionKeysDirectory => Path.Combine(ManagerDirectory, "keys");

    /// <summary>Mods the manager has disabled. Outside every path the game searches for mods.</summary>
    public string DisabledModsDirectory => Path.Combine(ManagerDirectory, "disabled-mods");

    /// <summary>Scratch space for downloads; on the same volume so files can be moved into place.</summary>
    public string TempDirectory => Path.Combine(ManagerDirectory, "tmp");

    /// <summary>Game server versions downloaded by the manager, one folder per version.</summary>
    public string GameVersionsDirectory => Path.Combine(Root, "game");

    /// <summary>Records which game version is active.</summary>
    public string GameStateFile => Path.Combine(ManagerDirectory, "game.json");

    public string ServerConfigPath => Path.Combine(GameDataDirectory, "serverconfig.json");

    public string LogsDirectory => Path.Combine(GameDataDirectory, "Logs");

    public string PlayerDataDirectory => Path.Combine(GameDataDirectory, "Playerdata");

    /// <summary>Creates the directory layout under <see cref="Root"/> if any part is missing.</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(GameDataDirectory);
        Directory.CreateDirectory(ModsDirectory);
        Directory.CreateDirectory(ManagerDirectory);
        Directory.CreateDirectory(DataProtectionKeysDirectory);
        Directory.CreateDirectory(DisabledModsDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(GameVersionsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
    }
}
