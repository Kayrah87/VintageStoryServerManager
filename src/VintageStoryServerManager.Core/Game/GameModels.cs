namespace VintageStoryServerManager.Core.Game;

/// <summary>A server release from the official version API.</summary>
public sealed record GameRelease(string Version, string Channel, string DownloadUrl, string? Md5, bool Latest);

/// <summary>A Vintage Story server installation the manager can run.</summary>
public sealed record GameInstall(string Version, string Directory, bool IsActive, bool IsBundled);

public interface IGameInstallation
{
    /// <summary>Directory containing VintagestoryServer.dll for the active version.</summary>
    string ActiveDirectory { get; }

    /// <summary>The active version, or null if it can't be determined.</summary>
    string? ActiveVersion { get; }

    IReadOnlyList<GameInstall> ListInstalled();

    Task<IReadOnlyList<GameRelease>> GetAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads, verifies and unpacks a server release into the data volume.</summary>
    Task InstallAsync(GameRelease release, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Makes an installed version the one the server runs. The server must be stopped.</summary>
    void Activate(string version);

    /// <summary>Removes a downloaded version. The bundled and the active version can't be removed.</summary>
    void Remove(string version);
}
