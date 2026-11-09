namespace VintageStoryServerManager.Core.Backups;

/// <summary>What a backup contains.</summary>
[Flags]
public enum BackupScope
{
    None = 0,

    /// <summary>Saves/ (worlds) and Playerdata/.</summary>
    World = 1,

    /// <summary>Mods/ and the manager's disabled mods.</summary>
    Mods = 2,

    /// <summary>serverconfig.json and ModConfig/.</summary>
    Config = 4,

    Full = World | Mods | Config,
}

public sealed record BackupInfo(
    string FileName,
    DateTimeOffset CreatedAt,
    BackupScope Scope,
    string? Label,
    bool Automatic,
    long SizeBytes,
    string? GameVersion);

public interface IBackupService
{
    Task<IReadOnlyList<BackupInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a backup. World files are copied with SQLite's online backup, so this is safe
    /// while the server is running.
    /// </summary>
    Task<BackupInfo> CreateAsync(BackupScope scope, string? label, bool automatic, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the backed-up parts of the data folder with the backup's contents.
    /// Requires the server to be stopped, and first makes an automatic backup of what it replaces.
    /// </summary>
    Task RestoreAsync(string fileName, CancellationToken cancellationToken = default);

    Task DeleteAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>Full path of a backup file, or null when it doesn't exist.</summary>
    string? GetPath(string fileName);
}
