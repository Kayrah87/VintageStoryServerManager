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
/// ├── manager/        manager.db (accounts, settings) and data-protection keys
/// └── backups/        world and mod backups made by the manager
/// </code>
/// The game binaries are not in the volume. They ship in the image at <see cref="GameInstallDirectory"/>.
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

    /// <summary>Creates the directory layout under <see cref="Root"/> if any part is missing.</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(GameDataDirectory);
        Directory.CreateDirectory(ModsDirectory);
        Directory.CreateDirectory(ManagerDirectory);
        Directory.CreateDirectory(DataProtectionKeysDirectory);
        Directory.CreateDirectory(BackupsDirectory);
    }
}
