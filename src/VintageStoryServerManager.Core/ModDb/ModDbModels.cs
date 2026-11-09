using VintageStoryServerManager.Core.Versioning;

namespace VintageStoryServerManager.Core.ModDb;

public sealed record ModDbSearchResult(
    int ModId,
    string Name,
    string? Summary,
    string? Author,
    IReadOnlyList<string> ModIdStrings,
    string? UrlAlias,
    string? Side,
    string? Logo,
    IReadOnlyList<string> Tags,
    int Downloads,
    string? LastReleased)
{
    /// <summary>The identifier to look the mod up by: its mod id string, alias or numeric id.</summary>
    public string Key => ModIdStrings.FirstOrDefault() ?? UrlAlias ?? ModId.ToString();
}

public sealed record ModDbRelease(
    int ReleaseId,
    string ModIdStr,
    string ModVersion,
    string FileName,
    string DownloadUrl,
    IReadOnlyList<string> GameVersions,
    string? Created);

public sealed record ModDbMod(
    int ModId,
    string Name,
    string? Author,
    string? UrlAlias,
    string? Side,
    IReadOnlyList<ModDbRelease> Releases)
{
    public string PageUrl(string baseUrl) => $"{baseUrl.TrimEnd('/')}/{UrlAlias ?? $"show/mod/{ModId}"}";
}

public interface IModDbClient
{
    Task<IReadOnlyList<ModDbSearchResult>> SearchAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Looks a mod up by mod id string (e.g. <c>exlib</c>), URL alias or numeric id.</summary>
    Task<ModDbMod?> GetModAsync(string idOrAlias, CancellationToken cancellationToken = default);

    Task<Stream> DownloadAsync(ModDbRelease release, CancellationToken cancellationToken = default);
}

public sealed record CompatibleRelease(ModDbRelease Release, bool ExactGameVersion);

public static class ReleaseSelector
{
    /// <summary>
    /// The newest release tagged for <paramref name="gameVersion"/>; otherwise the newest tagged
    /// for the same major.minor game version (flagged as not exact). Null when neither exists.
    /// </summary>
    public static CompatibleRelease? PickCompatible(IEnumerable<ModDbRelease> releases, string? gameVersion)
    {
        var ordered = releases.OrderByDescending(r => r.ModVersion, Comparer<string>.Create(ModVersion.Compare)).ToList();
        if (gameVersion is null)
        {
            return ordered.Count > 0 ? new(ordered[0], false) : null;
        }

        var exact = ordered.FirstOrDefault(r => r.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return new(exact, true);
        }

        var minor = ModVersion.MajorMinor(gameVersion);
        var close = ordered.FirstOrDefault(r => r.GameVersions.Any(v => ModVersion.MajorMinor(v) == minor));
        return close is null ? null : new(close, false);
    }
}
