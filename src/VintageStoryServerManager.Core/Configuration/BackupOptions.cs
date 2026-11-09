namespace VintageStoryServerManager.Core.Configuration;

public sealed class BackupOptions
{
    public const string SectionName = "Backups";

    /// <summary>Automatic backups (made before changes) to keep. Manual backups are never pruned.</summary>
    public int KeepAutomatic { get; set; } = 20;
}
