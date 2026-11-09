using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VintageStoryServerManager.Core.Backups;
using VintageStoryServerManager.Core.Configuration;
using VintageStoryServerManager.Core.ModDb;
using VintageStoryServerManager.Core.Mods;
using VintageStoryServerManager.Core.Versioning;
using VintageStoryServerManager.Infrastructure.Mods;

namespace VintageStoryServerManager.Infrastructure.ModDb;

public sealed record InstallStep(string ModId, string Name, ModDbRelease Release, string? ReplacesVersion, string Reason, bool ExactGameVersion);

/// <summary>
/// What an install or update will do. The packages are already downloaded and checked;
/// <see cref="ModInstaller.ApplyAsync"/> moves them into place, <see cref="ModInstaller.Discard"/> throws them away.
/// </summary>
public sealed class InstallPlan
{
    internal Dictionary<string, string> DownloadedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public required string RootModId { get; init; }

    public List<InstallStep> Steps { get; } = [];

    public List<string> Warnings { get; } = [];

    public List<string> Errors { get; } = [];

    public bool CanApply => Errors.Count == 0 && Steps.Count > 0;
}

public sealed record ModUpdate(string ModId, string Name, string FileName, string? InstalledVersion, string AvailableVersion, bool ExactGameVersion);

/// <summary>Installs and updates mods from the mod database, resolving dependencies.</summary>
public sealed class ModInstaller(
    IModDbClient modDb,
    ModLibrary library,
    IBackupService backups,
    MaintenanceLock maintenance,
    IOptions<DataPathOptions> paths,
    ILogger<ModInstaller> logger)
{
    private const int MaxSteps = 50;

    /// <summary>
    /// Works out what installing <paramref name="modKey"/> (a mod id string, alias or numeric id)
    /// involves: the mod itself plus any dependency that is missing or too old.
    /// </summary>
    public async Task<InstallPlan> PlanAsync(string modKey, CancellationToken cancellationToken = default)
    {
        var report = library.Scan();
        var gameVersion = report.GameVersion;
        var installed = report.Mods
            .Where(m => m.Enabled && m.Info is not null)
            .GroupBy(m => m.ModId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Info!.Version, StringComparer.OrdinalIgnoreCase);

        var plan = new InstallPlan { RootModId = modKey };
        var queue = new Queue<(string Key, string? MinVersion, string? RequiredBy)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        queue.Enqueue((modKey, null, null));

        try
        {
            while (queue.Count > 0 && plan.Steps.Count < MaxSteps)
            {
                var (key, minVersion, requiredBy) = queue.Dequeue();
                if (!visited.Add(key))
                {
                    continue;
                }

                var mod = await modDb.GetModAsync(key, cancellationToken);
                if (mod is null)
                {
                    plan.Errors.Add(requiredBy is null
                        ? $"'{key}' was not found on the mod database."
                        : $"'{requiredBy}' needs '{key}', which was not found on the mod database. Install it by hand.");
                    continue;
                }

                var candidates = mod.Releases.Where(r => minVersion is null || ModVersion.Satisfies(r.ModVersion, minVersion));
                var pick = ReleaseSelector.PickCompatible(candidates, gameVersion);
                if (pick is null)
                {
                    plan.Errors.Add($"'{mod.Name}' has no release for game version {gameVersion ?? "(unknown)"}" +
                        (minVersion is null ? "." : $" that is {minVersion} or newer."));
                    continue;
                }

                var release = pick.Release;
                var modId = string.IsNullOrEmpty(release.ModIdStr) ? key : release.ModIdStr;
                installed.TryGetValue(modId, out var current);

                if (requiredBy is null && current is not null && ModVersion.Compare(current, release.ModVersion) >= 0)
                {
                    plan.Warnings.Add($"'{mod.Name}' {current} is already installed and up to date.");
                    continue;
                }

                var file = await DownloadAsync(release, cancellationToken);
                ModInfo info;
                try
                {
                    info = ModLibrary.ReadPackage(file);
                }
                catch (InvalidDataException ex)
                {
                    File.Delete(file);
                    plan.Errors.Add($"The download of '{mod.Name}' {release.ModVersion} is not a usable mod: {ex.Message}");
                    continue;
                }

                plan.DownloadedFiles[info.ModId] = file;
                if (!string.Equals(info.ModId, modId, StringComparison.OrdinalIgnoreCase))
                {
                    installed.TryGetValue(info.ModId, out current);
                }

                if (!pick.ExactGameVersion)
                {
                    plan.Warnings.Add($"'{mod.Name}' {release.ModVersion} isn't tagged for game version {gameVersion}, only for {string.Join(", ", release.GameVersions)}.");
                }

                plan.Steps.Add(new InstallStep(
                    info.ModId,
                    mod.Name,
                    release,
                    current,
                    requiredBy is null ? (current is null ? "Requested" : "Update") : $"Needed by {requiredBy}",
                    pick.ExactGameVersion));
                installed[info.ModId] = info.Version ?? release.ModVersion;

                foreach (var (depId, required) in info.Dependencies)
                {
                    if (ModHealthChecker.BuiltInModIds.Contains(depId))
                    {
                        if (gameVersion is not null && !ModVersion.Satisfies(gameVersion, required))
                        {
                            plan.Errors.Add($"'{mod.Name}' {release.ModVersion} needs game version {required} or newer; the server runs {gameVersion}.");
                        }

                        continue;
                    }

                    if (installed.TryGetValue(depId, out var have) && ModVersion.Satisfies(have, required))
                    {
                        continue;
                    }

                    queue.Enqueue((depId, string.IsNullOrWhiteSpace(required) || required == "*" ? null : required, mod.Name));
                }
            }
        }
        catch
        {
            Discard(plan);
            throw;
        }

        return plan;
    }

    /// <summary>Backs up the mods, then installs every step of the plan.</summary>
    public async Task<BackupInfo> ApplyAsync(InstallPlan plan, CancellationToken cancellationToken = default)
    {
        if (!plan.CanApply)
        {
            throw new InvalidOperationException("This plan has errors or nothing to install.");
        }

        try
        {
            using var _ = await maintenance.AcquireAsync(cancellationToken);
            var names = string.Join(", ", plan.Steps.Select(s => s.ModId));
            var backup = await backups.CreateAsync(BackupScope.Mods, $"before installing {names}", automatic: true, cancellationToken);

            foreach (var step in plan.Steps)
            {
                library.PlacePackage(plan.DownloadedFiles[step.ModId], step.Release.FileName, step.ModId);
                logger.LogInformation("Installed {ModId} {Version} from the mod database", step.ModId, step.Release.ModVersion);
            }

            return backup;
        }
        finally
        {
            Discard(plan);
        }
    }

    public void Discard(InstallPlan plan)
    {
        foreach (var file in plan.DownloadedFiles.Values)
        {
            File.Delete(file);
        }

        plan.DownloadedFiles.Clear();
    }

    /// <summary>Installed mods with a newer release for the current game version on the mod database.</summary>
    public async Task<IReadOnlyList<ModUpdate>> CheckUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var report = library.Scan();
        var mods = report.Mods.Where(m => m.Enabled && m.Info is not null).ToList();
        var updates = new List<ModUpdate>();
        using var throttle = new SemaphoreSlim(4);

        await Task.WhenAll(mods.Select(async mod =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var remote = await modDb.GetModAsync(mod.ModId!, cancellationToken);
                var pick = remote is null ? null : ReleaseSelector.PickCompatible(remote.Releases, report.GameVersion);
                if (pick is not null && ModVersion.Compare(pick.Release.ModVersion, mod.Info!.Version) > 0)
                {
                    lock (updates)
                    {
                        updates.Add(new ModUpdate(mod.ModId!, mod.DisplayName, mod.FileName, mod.Info.Version, pick.Release.ModVersion, pick.ExactGameVersion));
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Could not check {ModId} for updates", mod.ModId);
            }
            finally
            {
                throttle.Release();
            }
        }));

        return updates.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<string> DownloadAsync(ModDbRelease release, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Value.TempDirectory);
        var file = Path.Combine(paths.Value.TempDirectory, $"{Guid.NewGuid():N}.zip");
        await using var source = await modDb.DownloadAsync(release, cancellationToken);
        await using var target = File.Create(file);
        await source.CopyToAsync(target, cancellationToken);
        return file;
    }
}
