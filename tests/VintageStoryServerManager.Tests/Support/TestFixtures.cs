using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VintageStoryServerManager.Core.Backups;
using VintageStoryServerManager.Core.Configuration;
using VintageStoryServerManager.Core.Game;
using VintageStoryServerManager.Core.ModDb;
using VintageStoryServerManager.Core.Server;
using VintageStoryServerManager.Infrastructure;
using VintageStoryServerManager.Infrastructure.Backups;
using VintageStoryServerManager.Infrastructure.Mods;

namespace VintageStoryServerManager.Tests.Support;

/// <summary>A throwaway data volume with the manager's layout.</summary>
internal sealed class TempDataRoot : IDisposable
{
    public TempDataRoot()
    {
        Paths = new DataPathOptions
        {
            Root = Path.Combine(Path.GetTempPath(), "vssm-tests", Guid.NewGuid().ToString("N")),
        };
        Paths.GameInstallDirectory = Path.Combine(Paths.Root, "bundled-game");
        Paths.EnsureDirectories();
    }

    public DataPathOptions Paths { get; }

    public IOptions<DataPathOptions> Options => Microsoft.Extensions.Options.Options.Create(Paths);

    public void Dispose()
    {
        if (Directory.Exists(Paths.Root))
        {
            Directory.Delete(Paths.Root, recursive: true);
        }
    }
}

internal sealed class FakeGameServer : IGameServer
{
    public ServerSnapshot Snapshot { get; set; } = new(ServerStatus.Stopped, null, null, null, [], null, null);

    public List<string> Commands { get; } = [];

    public event Action<ServerSnapshot>? StatusChanged;

    public event Action<ConsoleLine>? ConsoleOutput;

    public void SetStatus(ServerStatus status)
    {
        Snapshot = Snapshot with { Status = status };
        StatusChanged?.Invoke(Snapshot);
        ConsoleOutput?.Invoke(new ConsoleLine(DateTimeOffset.UtcNow, status.ToString(), ConsoleLineKind.Manager));
    }

    public IReadOnlyList<ConsoleLine> ConsoleHistory() => [];

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        SetStatus(ServerStatus.Running);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        SetStatus(ServerStatus.Stopped);
        return Task.CompletedTask;
    }

    public Task RestartAsync(CancellationToken cancellationToken = default) => StartAsync(cancellationToken);

    public Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (!Snapshot.IsRunning)
        {
            throw new InvalidOperationException("The server isn't running.");
        }

        Commands.Add(command);
        return Task.CompletedTask;
    }
}

internal sealed class FakeGameInstallation(string? version = "1.22.7", string directory = "/opt/vintagestory") : IGameInstallation
{
    public string ActiveDirectory => directory;

    public string? ActiveVersion { get; set; } = version;

    public IReadOnlyList<GameInstall> ListInstalled() => [new GameInstall(ActiveVersion ?? "?", directory, true, true)];

    public Task<IReadOnlyList<GameRelease>> GetAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GameRelease>>([]);

    public Task InstallAsync(GameRelease release, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public void Activate(string version) => ActiveVersion = version;

    public void Remove(string version)
    {
    }
}

/// <summary>An in-memory mod database: mod id → releases, each release backed by a zip in memory.</summary>
internal sealed class FakeModDb : IModDbClient
{
    private readonly Dictionary<string, ModDbMod> _mods = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _files = new();

    public int Downloads { get; private set; }

    public FakeModDb Add(string modId, string version, string[] gameVersions, Dictionary<string, string>? dependencies = null)
    {
        var url = $"https://example.test/{modId}_{version}.zip";
        _files[url] = TestZips.Mod(modId, version, dependencies);
        var release = new ModDbRelease(_files.Count, modId, version, $"{modId}_{version}.zip", url, gameVersions, null);

        var existing = _mods.TryGetValue(modId, out var mod) ? mod.Releases : [];
        _mods[modId] = new ModDbMod(_mods.Count + 1, $"{modId} mod", "author", modId, "both", [release, .. existing]);
        return this;
    }

    public Task<IReadOnlyList<ModDbSearchResult>> SearchAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModDbSearchResult>>(_mods.Values
            .Where(m => m.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(m => new ModDbSearchResult(m.ModId, m.Name, null, m.Author, [m.UrlAlias!], m.UrlAlias, m.Side, null, [], 0, null))
            .ToList());

    public Task<ModDbMod?> GetModAsync(string idOrAlias, CancellationToken cancellationToken = default) =>
        Task.FromResult(_mods.GetValueOrDefault(idOrAlias));

    public Task<Stream> DownloadAsync(ModDbRelease release, CancellationToken cancellationToken = default)
    {
        Downloads++;
        return Task.FromResult<Stream>(new MemoryStream(_files[release.DownloadUrl]));
    }
}

internal static class TestZips
{
    public static byte[] Mod(string modId, string version, Dictionary<string, string>? dependencies = null, string? rawModInfo = null)
    {
        var deps = dependencies is null ? "" : $", \"dependencies\": {{ {string.Join(", ", dependencies.Select(d => $"\"{d.Key}\": \"{d.Value}\""))} }}";
        var modinfo = rawModInfo ?? $"{{ \"type\": \"code\", \"modid\": \"{modId}\", \"name\": \"{modId} mod\", \"version\": \"{version}\"{deps} }}";

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("modinfo.json");
            using (var writer = new StreamWriter(entry.Open(), Encoding.UTF8))
            {
                writer.Write(modinfo);
            }

            var dll = zip.CreateEntry($"{modId}.dll");
            using var stream = dll.Open();
            stream.Write("not really a dll"u8);
        }

        return buffer.ToArray();
    }

    public static string WriteMod(string directory, string fileName, string modId, string version, Dictionary<string, string>? dependencies = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, Mod(modId, version, dependencies));
        return path;
    }
}

/// <summary>Wires the real backup service and mod library over a temp data root.</summary>
internal sealed class ModTestContext : IDisposable
{
    public ModTestContext(string? gameVersion = "1.22.7")
    {
        Installation = new FakeGameInstallation(gameVersion);
        Backups = new BackupService(Root.Options, Microsoft.Extensions.Options.Options.Create(new BackupOptions()), Installation, Server,
            TimeProvider.System, NullLogger<BackupService>.Instance);
        Library = new ModLibrary(Root.Options, Installation, Server, Backups, Lock, NullLogger<ModLibrary>.Instance);
    }

    public TempDataRoot Root { get; } = new();

    public FakeGameServer Server { get; } = new();

    public FakeGameInstallation Installation { get; }

    public MaintenanceLock Lock { get; } = new();

    public BackupService Backups { get; }

    public ModLibrary Library { get; }

    public DataPathOptions Paths => Root.Paths;

    public void Dispose() => Root.Dispose();
}
