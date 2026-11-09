using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VintageStoryServerManager.Core.Backups;
using VintageStoryServerManager.Core.Configuration;
using VintageStoryServerManager.Core.Game;
using VintageStoryServerManager.Core.Mods;
using VintageStoryServerManager.Core.Server;

namespace VintageStoryServerManager.Infrastructure.Mods;

/// <summary>
/// The installed mods: scanning and health checks, and file operations (enable, disable,
/// delete, upload). Every change makes an automatic Mods backup first.
/// </summary>
public sealed class ModLibrary(
    IOptions<DataPathOptions> paths,
    IGameInstallation installation,
    IGameServer server,
    IBackupService backups,
    MaintenanceLock maintenance,
    ILogger<ModLibrary> logger)
{
    private DataPathOptions Paths => paths.Value;

    /// <summary>The game version mods are checked against: the active install, or what the server reported.</summary>
    public string? GameVersion => installation.ActiveVersion ?? server.Snapshot.GameVersion;

    public ModScanReport Scan()
    {
        var mods = new List<InstalledMod>();
        mods.AddRange(ScanDirectory(Paths.ModsDirectory, enabled: true));
        mods.AddRange(ScanDirectory(Paths.DisabledModsDirectory, enabled: false));
        mods.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        var version = GameVersion;
        return new ModScanReport(mods, ModHealthChecker.Check(mods, version), version);
    }

    public async Task DisableAsync(string fileName, CancellationToken cancellationToken = default)
    {
        using var _ = await maintenance.AcquireAsync(cancellationToken);
        var source = Resolve(Paths.ModsDirectory, fileName);
        await backups.CreateAsync(BackupScope.Mods, $"before disabling {fileName}", automatic: true, cancellationToken);
        Move(source, Path.Combine(Paths.DisabledModsDirectory, fileName));
        logger.LogInformation("Disabled mod {File}", fileName);
    }

    public async Task EnableAsync(string fileName, CancellationToken cancellationToken = default)
    {
        using var _ = await maintenance.AcquireAsync(cancellationToken);
        var source = Resolve(Paths.DisabledModsDirectory, fileName);
        await backups.CreateAsync(BackupScope.Mods, $"before enabling {fileName}", automatic: true, cancellationToken);
        Move(source, Path.Combine(Paths.ModsDirectory, fileName));
        logger.LogInformation("Enabled mod {File}", fileName);
    }

    public async Task DeleteAsync(string fileName, bool enabled, CancellationToken cancellationToken = default)
    {
        using var _ = await maintenance.AcquireAsync(cancellationToken);
        var path = Resolve(enabled ? Paths.ModsDirectory : Paths.DisabledModsDirectory, fileName);
        await backups.CreateAsync(BackupScope.Mods, $"before deleting {fileName}", automatic: true, cancellationToken);
        DeletePath(path);
        logger.LogInformation("Deleted mod {File}", fileName);
    }

    /// <summary>
    /// Installs an uploaded mod zip. Other copies of the same mod id are removed, so this also
    /// handles updates and downgrades.
    /// </summary>
    public async Task<ModInfo> InstallUploadAsync(Stream content, string fileName, CancellationToken cancellationToken = default)
    {
        fileName = Path.GetFileName(fileName);
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Upload a mod .zip file.");
        }

        Directory.CreateDirectory(Paths.TempDirectory);
        var temp = Path.Combine(Paths.TempDirectory, $"{Guid.NewGuid():N}.zip");
        try
        {
            await using (var file = File.Create(temp))
            {
                await content.CopyToAsync(file, cancellationToken);
            }

            var info = ReadPackage(temp);
            using var _ = await maintenance.AcquireAsync(cancellationToken);
            await backups.CreateAsync(BackupScope.Mods, $"before installing {fileName}", automatic: true, cancellationToken);
            PlacePackage(temp, fileName, info.ModId);
            logger.LogInformation("Installed uploaded mod {ModId} {Version} as {File}", info.ModId, info.Version, fileName);
            return info;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Validates a downloaded or uploaded zip and returns its metadata.</summary>
    public static ModInfo ReadPackage(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return ModInfoReader.ReadFromZip(zip)
                ?? throw new InvalidDataException("The zip has no modinfo.json, so it isn't a mod package.");
        }
        catch (InvalidDataException ex) when (ex.InnerException is null && ex.Message.Contains("End of Central Directory", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The file is not a valid zip archive.", ex);
        }
    }

    /// <summary>
    /// Moves a validated package into Mods, removing every other enabled or disabled copy of
    /// the same mod id. Caller holds the maintenance lock and has made a backup.
    /// </summary>
    internal void PlacePackage(string packagePath, string fileName, string modId)
    {
        foreach (var existing in ScanDirectory(Paths.ModsDirectory, true).Concat(ScanDirectory(Paths.DisabledModsDirectory, false)))
        {
            if (string.Equals(existing.ModId, modId, StringComparison.OrdinalIgnoreCase))
            {
                DeletePath(existing.FullPath);
            }
        }

        var target = Path.Combine(Paths.ModsDirectory, Path.GetFileName(fileName));
        if (File.Exists(target))
        {
            // Same file name, different mod id: keep both rather than overwrite.
            target = Path.Combine(Paths.ModsDirectory, $"{Path.GetFileNameWithoutExtension(fileName)}-{Guid.NewGuid().ToString("N")[..6]}.zip");
        }

        File.Move(packagePath, target);
    }

    private static IEnumerable<InstalledMod> ScanDirectory(string directory, bool enabled)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (entry.Name.StartsWith('.'))
            {
                continue;
            }

            yield return Read(entry, enabled);
        }
    }

    private static InstalledMod Read(FileSystemInfo entry, bool enabled)
    {
        var kind = entry switch
        {
            DirectoryInfo => ModPackageKind.Folder,
            _ when entry.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) => ModPackageKind.Zip,
            _ when entry.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) => ModPackageKind.Assembly,
            _ when entry.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) => ModPackageKind.SourceFile,
            _ => ModPackageKind.Unknown,
        };

        long size = entry switch
        {
            FileInfo file => file.Length,
            DirectoryInfo dir => dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length),
            _ => 0,
        };

        try
        {
            var info = kind switch
            {
                ModPackageKind.Zip => ModInfoReader.ReadFromZip(entry.FullName),
                ModPackageKind.Folder => ModInfoReader.ReadFromFolder(entry.FullName),
                _ => null,
            };
            return new InstalledMod(entry.Name, entry.FullName, kind, enabled, size, info, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new InstalledMod(entry.Name, entry.FullName, kind, enabled, size, null, ex.Message);
        }
    }

    private static string Resolve(string directory, string fileName)
    {
        if (fileName != Path.GetFileName(fileName) || fileName is "." or "..")
        {
            throw new ArgumentException($"Invalid mod file name '{fileName}'.", nameof(fileName));
        }

        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException($"Mod '{fileName}' not found.", path);
        }

        return path;
    }

    private static void Move(string source, string target)
    {
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new IOException($"'{Path.GetFileName(target)}' already exists there.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (Directory.Exists(source))
        {
            Directory.Move(source, target);
        }
        else
        {
            File.Move(source, target);
        }
    }

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else
        {
            File.Delete(path);
        }
    }
}
