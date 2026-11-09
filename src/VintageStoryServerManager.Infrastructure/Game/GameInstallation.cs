using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VintageStoryServerManager.Core.Configuration;
using VintageStoryServerManager.Core.Game;
using VintageStoryServerManager.Core.Versioning;

namespace VintageStoryServerManager.Infrastructure.Game;

/// <summary>
/// Tracks the bundled server install and versions downloaded into the data volume, and
/// which one is active. The active version is stored in <c>/data/manager/game.json</c>.
/// </summary>
public sealed class GameInstallation(
    IOptions<DataPathOptions> paths,
    IOptions<ModDbOptions> apiOptions,
    IHttpClientFactory httpFactory,
    ILogger<GameInstallation> logger) : IGameInstallation
{
    public const string HttpClientName = "game-downloads";

    public const string ServerAssembly = "VintagestoryServer.dll";

    /// <summary>Written next to the server files with the version they belong to.</summary>
    public const string VersionFile = ".vsversion";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private DataPathOptions Paths => paths.Value;

    public string ActiveDirectory => ResolveActive().Directory;

    public string? ActiveVersion => ResolveActive().Version;

    public IReadOnlyList<GameInstall> ListInstalled()
    {
        var active = ResolveActive();
        var installs = new List<GameInstall>();

        if (File.Exists(Path.Combine(Paths.GameInstallDirectory, ServerAssembly)))
        {
            installs.Add(new GameInstall(
                ReadVersion(Paths.GameInstallDirectory) ?? "bundled",
                Paths.GameInstallDirectory,
                SamePath(active.Directory, Paths.GameInstallDirectory),
                IsBundled: true));
        }

        if (Directory.Exists(Paths.GameVersionsDirectory))
        {
            foreach (var dir in Directory.EnumerateDirectories(Paths.GameVersionsDirectory))
            {
                if (File.Exists(Path.Combine(dir, ServerAssembly)) && !dir.EndsWith(".partial", StringComparison.Ordinal))
                {
                    installs.Add(new GameInstall(
                        ReadVersion(dir) ?? Path.GetFileName(dir),
                        dir,
                        SamePath(active.Directory, dir),
                        IsBundled: false));
                }
            }
        }

        return installs
            .OrderByDescending(i => i.Version, Comparer<string>.Create(ModVersion.Compare))
            .ToList();
    }

    public async Task<IReadOnlyList<GameRelease>> GetAvailableAsync(CancellationToken cancellationToken = default)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        var releases = new List<GameRelease>();
        foreach (var channel in new[] { "stable", "unstable" })
        {
            try
            {
                var url = new Uri(new Uri(apiOptions.Value.GameApiUrl), $"{channel}.json");
                var root = await http.GetFromJsonAsync<JsonObject>(url, cancellationToken);
                foreach (var (version, node) in root ?? [])
                {
                    if (node?["linuxserver"] is JsonObject server && server["urls"]?["cdn"]?.GetValue<string>() is { } downloadUrl)
                    {
                        releases.Add(new GameRelease(
                            version,
                            channel,
                            downloadUrl,
                            server["md5"]?.GetValue<string>(),
                            server["latest"]?.GetValueKind() == JsonValueKind.Number && server["latest"]!.GetValue<int>() == 1));
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Could not load {Channel} game versions", channel);
            }
        }

        return releases
            .OrderByDescending(r => r.Version, Comparer<string>.Create(ModVersion.Compare))
            .ToList();
    }

    public async Task InstallAsync(GameRelease release, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var target = VersionDirectory(release.Version);
        if (File.Exists(Path.Combine(target, ServerAssembly)))
        {
            progress?.Report($"Version {release.Version} is already installed.");
            return;
        }

        Directory.CreateDirectory(Paths.TempDirectory);
        var archive = Path.Combine(Paths.TempDirectory, $"vs_server_{release.Version}_{Guid.NewGuid():N}.tar.gz");
        var partial = target + ".partial";

        try
        {
            progress?.Report($"Downloading {release.Version}…");
            var http = httpFactory.CreateClient(HttpClientName);
            await using (var download = await http.GetStreamAsync(release.DownloadUrl, cancellationToken))
            await using (var file = File.Create(archive))
            {
                await download.CopyToAsync(file, cancellationToken);
            }

            if (!string.IsNullOrEmpty(release.Md5))
            {
                progress?.Report("Verifying checksum…");
                await using var file = File.OpenRead(archive);
                var actual = Convert.ToHexStringLower(await MD5.HashDataAsync(file, cancellationToken));
                if (!string.Equals(actual, release.Md5, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Checksum mismatch for {release.Version}: expected {release.Md5}, got {actual}.");
                }
            }

            progress?.Report("Unpacking…");
            if (Directory.Exists(partial))
            {
                Directory.Delete(partial, recursive: true);
            }

            Directory.CreateDirectory(partial);
            await using (var file = File.OpenRead(archive))
            await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            {
                await TarFile.ExtractToDirectoryAsync(gzip, partial, overwriteFiles: true, cancellationToken);
            }

            if (!File.Exists(Path.Combine(partial, ServerAssembly)))
            {
                throw new InvalidDataException($"The {release.Version} download doesn't contain {ServerAssembly}.");
            }

            await File.WriteAllTextAsync(Path.Combine(partial, VersionFile), release.Version, cancellationToken);
            Directory.Move(partial, target);
            progress?.Report($"Installed {release.Version}.");
            logger.LogInformation("Installed Vintage Story server {Version} into {Directory}", release.Version, target);
        }
        finally
        {
            File.Delete(archive);
            if (Directory.Exists(partial))
            {
                Directory.Delete(partial, recursive: true);
            }
        }
    }

    public void Activate(string version)
    {
        var install = ListInstalled().FirstOrDefault(i => i.Version == version)
            ?? throw new InvalidOperationException($"Version {version} is not installed.");

        var state = new GameState(install.IsBundled ? null : install.Version);
        Directory.CreateDirectory(Paths.ManagerDirectory);
        File.WriteAllText(Paths.GameStateFile, JsonSerializer.Serialize(state, JsonOptions));
        logger.LogInformation("Active Vintage Story server version is now {Version}", version);
    }

    public void Remove(string version)
    {
        var install = ListInstalled().FirstOrDefault(i => i.Version == version)
            ?? throw new InvalidOperationException($"Version {version} is not installed.");
        if (install.IsBundled)
        {
            throw new InvalidOperationException("The version bundled with the image can't be removed.");
        }

        if (install.IsActive)
        {
            throw new InvalidOperationException("The active version can't be removed. Activate another version first.");
        }

        Directory.Delete(install.Directory, recursive: true);
    }

    private (string Directory, string? Version) ResolveActive()
    {
        var state = ReadState();
        if (state?.ActiveVersion is { } version)
        {
            var dir = VersionDirectory(version);
            if (File.Exists(Path.Combine(dir, ServerAssembly)))
            {
                return (dir, ReadVersion(dir) ?? version);
            }
        }

        return (Paths.GameInstallDirectory, ReadVersion(Paths.GameInstallDirectory));
    }

    private GameState? ReadState()
    {
        try
        {
            return File.Exists(Paths.GameStateFile)
                ? JsonSerializer.Deserialize<GameState>(File.ReadAllText(Paths.GameStateFile))
                : null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Ignoring unreadable {File}", Paths.GameStateFile);
            return null;
        }
    }

    private string VersionDirectory(string version)
    {
        if (version.Contains('/') || version.Contains('\\') || version.Contains(".."))
        {
            throw new ArgumentException($"Invalid version '{version}'.", nameof(version));
        }

        return Path.Combine(Paths.GameVersionsDirectory, version);
    }

    private static string? ReadVersion(string directory)
    {
        var file = Path.Combine(directory, VersionFile);
        return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('/'), Path.GetFullPath(b).TrimEnd('/'), StringComparison.Ordinal);

    private sealed record GameState(string? ActiveVersion);
}
