using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VintageStoryServerManager.Core.Backups;
using VintageStoryServerManager.Core.Configuration;
using VintageStoryServerManager.Core.Game;
using VintageStoryServerManager.Core.Server;

namespace VintageStoryServerManager.Infrastructure.Backups;

/// <summary>
/// Zip backups of parts of the data folder. Each zip holds a <c>backup.json</c> manifest,
/// game data under <c>vintagestory/</c> and disabled mods under <c>disabled-mods/</c>.
/// </summary>
public sealed partial class BackupService(
    IOptions<DataPathOptions> paths,
    IOptions<BackupOptions> options,
    IGameInstallation installation,
    IGameServer server,
    TimeProvider time,
    ILogger<BackupService> logger) : IBackupService
{
    private const string ManifestName = "backup.json";
    private const string GamePrefix = "vintagestory/";
    private const string DisabledModsPrefix = "disabled-mods/";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Restores and backups must not interleave; held only inside this service, so callers
    // holding the MaintenanceLock can still create backups.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DataPathOptions Paths => paths.Value;

    public Task<IReadOnlyList<BackupInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Paths.BackupsDirectory))
        {
            return Task.FromResult<IReadOnlyList<BackupInfo>>([]);
        }

        var backups = Directory.EnumerateFiles(Paths.BackupsDirectory, "*.zip")
            .Select(ReadInfo)
            .OfType<BackupInfo>()
            .OrderByDescending(b => b.CreatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<BackupInfo>>(backups);
    }

    public async Task<BackupInfo> CreateAsync(BackupScope scope, string? label, bool automatic, CancellationToken cancellationToken = default)
    {
        if (scope == BackupScope.None)
        {
            throw new ArgumentException("Choose what to back up.", nameof(scope));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await CreateCoreAsync(scope, label, automatic, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestoreAsync(string fileName, CancellationToken cancellationToken = default)
    {
        if (server.Snapshot.IsRunning || server.Snapshot.Status == ServerStatus.Stopping)
        {
            throw new InvalidOperationException("Stop the server before restoring a backup.");
        }

        var path = GetPath(fileName) ?? throw new FileNotFoundException($"Backup '{fileName}' not found.");
        var info = ReadInfo(path) ?? throw new InvalidDataException($"'{fileName}' is not a backup made by this manager.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await CreateCoreAsync(info.Scope, $"before restoring {fileName}", automatic: true, cancellationToken);

            foreach (var target in Targets(info.Scope))
            {
                DeleteIfExists(target);
            }

            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName == ManifestName || entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                var destination = MapEntry(entry.FullName);
                if (destination is null)
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            logger.LogInformation("Restored backup {File} ({Scope})", fileName, info.Scope);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task DeleteAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var path = GetPath(fileName) ?? throw new FileNotFoundException($"Backup '{fileName}' not found.");
        File.Delete(path);
        return Task.CompletedTask;
    }

    public string? GetPath(string fileName)
    {
        if (fileName != Path.GetFileName(fileName) || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = Path.Combine(Paths.BackupsDirectory, fileName);
        return File.Exists(path) ? path : null;
    }

    private async Task<BackupInfo> CreateCoreAsync(BackupScope scope, string? label, bool automatic, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Paths.BackupsDirectory);
        Directory.CreateDirectory(Paths.TempDirectory);

        var now = time.GetUtcNow();
        var name = $"{now:yyyyMMdd-HHmmss}-{scope.ToString().Replace(", ", "+").ToLowerInvariant()}-{(automatic ? "auto" : "manual")}";
        if (!string.IsNullOrWhiteSpace(label))
        {
            name += "-" + Slug(label);
        }

        var target = Path.Combine(Paths.BackupsDirectory, name + ".zip");
        for (var i = 2; File.Exists(target); i++)
        {
            target = Path.Combine(Paths.BackupsDirectory, $"{name}-{i}.zip");
        }

        var partial = Path.Combine(Paths.TempDirectory, $"{Guid.NewGuid():N}.zip.partial");
        var manifest = new Manifest(now, scope, label?.Trim(), automatic, installation.ActiveVersion ?? server.Snapshot.GameVersion);

        try
        {
            await using (var stream = File.Create(partial))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var manifestEntry = zip.CreateEntry(ManifestName);
                await using (var writer = manifestEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(writer, manifest, JsonOptions, cancellationToken);
                }

                foreach (var (source, entryPrefix) in Sources(scope))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.Exists(source))
                    {
                        await AddFileAsync(zip, source, entryPrefix + Path.GetFileName(source), cancellationToken);
                    }
                    else if (Directory.Exists(source))
                    {
                        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                        {
                            var relative = Path.GetRelativePath(Path.GetDirectoryName(source)!, file).Replace('\\', '/');
                            await AddFileAsync(zip, file, entryPrefix + relative, cancellationToken);
                        }
                    }
                }
            }

            File.Move(partial, target);
        }
        finally
        {
            File.Delete(partial);
        }

        logger.LogInformation("Created {Kind} backup {File}", automatic ? "automatic" : "manual", Path.GetFileName(target));
        if (automatic)
        {
            Prune();
        }

        return ReadInfo(target)!;
    }

    /// <summary>World databases are copied with SQLite's online backup so a running server is safe.</summary>
    private async Task AddFileAsync(ZipArchive zip, string file, string entryName, CancellationToken cancellationToken)
    {
        if (file.EndsWith("-wal", StringComparison.Ordinal) || file.EndsWith("-shm", StringComparison.Ordinal) ||
            file.EndsWith("-journal", StringComparison.Ordinal))
        {
            return;
        }

        if (file.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = Path.Combine(Paths.TempDirectory, $"{Guid.NewGuid():N}.vcdbs");
            try
            {
                SnapshotDatabase(file, snapshot);
                zip.CreateEntryFromFile(snapshot, entryName, CompressionLevel.Optimal);
            }
            finally
            {
                File.Delete(snapshot);
            }

            return;
        }

        // Shared read: the game may hold logs or configs open.
        await using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = File.GetLastWriteTime(file);
        await using var target = entry.Open();
        await source.CopyToAsync(target, cancellationToken);
    }

    private static void SnapshotDatabase(string source, string destination)
    {
        var sourceConnection = new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        var destinationConnection = new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false };

        using var from = new SqliteConnection(sourceConnection.ToString());
        using var to = new SqliteConnection(destinationConnection.ToString());
        from.Open();
        to.Open();
        from.BackupDatabase(to);

        // Keep the copy self-contained (no -wal file next to it).
        using var command = to.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE;";
        command.ExecuteNonQuery();
    }

    private IEnumerable<(string Source, string EntryPrefix)> Sources(BackupScope scope)
    {
        var game = Paths.GameDataDirectory;
        if (scope.HasFlag(BackupScope.World))
        {
            yield return (Path.Combine(game, "Saves"), GamePrefix);
            yield return (Paths.PlayerDataDirectory, GamePrefix);
        }

        if (scope.HasFlag(BackupScope.Mods))
        {
            yield return (Paths.ModsDirectory, GamePrefix);
            yield return (Paths.DisabledModsDirectory, "");
        }

        if (scope.HasFlag(BackupScope.Config))
        {
            yield return (Paths.ServerConfigPath, GamePrefix);
            yield return (Path.Combine(game, "ModConfig"), GamePrefix);
        }
    }

    private IEnumerable<string> Targets(BackupScope scope) => Sources(scope).Select(s => s.Source);

    /// <summary>Maps a zip entry back to its place in the data folder; rejects paths that escape it.</summary>
    private string? MapEntry(string entryName)
    {
        string root;
        string relative;
        if (entryName.StartsWith(GamePrefix, StringComparison.Ordinal))
        {
            root = Paths.GameDataDirectory;
            relative = entryName[GamePrefix.Length..];
        }
        else if (entryName.StartsWith(DisabledModsPrefix, StringComparison.Ordinal))
        {
            root = Paths.ManagerDirectory;
            relative = entryName;
        }
        else
        {
            return null;
        }

        var destination = Path.GetFullPath(Path.Combine(root, relative));
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        return destination.StartsWith(rootFull, StringComparison.Ordinal) ? destination : null;
    }

    private void Prune()
    {
        var keep = Math.Max(1, options.Value.KeepAutomatic);
        var automatic = Directory.EnumerateFiles(Paths.BackupsDirectory, "*.zip")
            .Select(ReadInfo)
            .OfType<BackupInfo>()
            .Where(b => b.Automatic)
            .OrderByDescending(b => b.CreatedAt)
            .Skip(keep);

        foreach (var old in automatic)
        {
            File.Delete(Path.Combine(Paths.BackupsDirectory, old.FileName));
            logger.LogInformation("Pruned automatic backup {File}", old.FileName);
        }
    }

    private BackupInfo? ReadInfo(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry(ManifestName);
            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            var manifest = JsonSerializer.Deserialize<Manifest>(stream);
            if (manifest is null)
            {
                return null;
            }

            return new BackupInfo(
                Path.GetFileName(path),
                manifest.CreatedAt,
                manifest.Scope,
                manifest.Label,
                manifest.Automatic,
                new FileInfo(path).Length,
                manifest.GameVersion);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            logger.LogWarning(ex, "Skipping unreadable backup {File}", path);
            return null;
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string Slug(string label)
    {
        var slug = NonSlug().Replace(label.Trim().ToLower(CultureInfo.InvariantCulture), "-").Trim('-');
        return slug.Length > 50 ? slug[..50].TrimEnd('-') : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    private sealed record Manifest(DateTimeOffset CreatedAt, BackupScope Scope, string? Label, bool Automatic, string? GameVersion);
}
