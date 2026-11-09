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
    string? Type,
    string? Logo,
    IReadOnlyList<string> Tags,
    int Downloads,
    int Follows,
    int TrendingPoints,
    int Comments,
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

public sealed record ModDbScreenshot(string Url, string? ThumbnailUrl);

public sealed record ModDbLinks(string? Homepage, string? Source, string? IssueTracker, string? Wiki, string? Trailer);

public sealed record ModDbMod(
    int ModId,
    string Name,
    string? Author,
    string? UrlAlias,
    string? Side,
    IReadOnlyList<ModDbRelease> Releases)
{
    public string? Logo { get; init; }

    public IReadOnlyList<ModDbScreenshot> Screenshots { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];

    public ModDbLinks Links { get; init; } = new(null, null, null, null, null);

    public int Downloads { get; init; }

    public int Follows { get; init; }

    public string? Created { get; init; }

    public string? LastReleased { get; init; }

    public string PageUrl(string baseUrl) => $"{baseUrl.TrimEnd('/')}/{UrlAlias ?? $"show/mod/{ModId}"}";
}

/// <summary>A tag or game version from the mod database (both are tags with numeric ids).</summary>
public sealed record ModDbTag(long Id, string Name, string? Color);

/// <summary>Sort orders the mod database supports.</summary>
public enum ModDbSort
{
    Trending,
    Newest,
    RecentlyUpdated,
    MostDownloaded,
    MostFollowed,
    MostComments,
}

/// <summary>A browse request. Sorting and the tag and game-version filters run on the mod database.</summary>
public sealed record ModDbQuery(
    string? Text = null,
    ModDbSort Sort = ModDbSort.Trending,
    bool Ascending = false,
    IReadOnlyList<long>? TagIds = null,
    long? GameVersionTagId = null)
{
    public static string OrderByParameter(ModDbSort sort) => sort switch
    {
        ModDbSort.Newest => "asset.created",
        ModDbSort.RecentlyUpdated => "lastreleased",
        ModDbSort.MostDownloaded => "downloads",
        ModDbSort.MostFollowed => "follows",
        ModDbSort.MostComments => "comments",
        _ => "trendingpoints",
    };

    /// <summary>The API path and query string, e.g. <c>api/mods?text=ruins&amp;orderby=downloads&amp;orderdirection=desc</c>.</summary>
    public string ToRelativeUrl()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Text))
        {
            parts.Add($"text={Uri.EscapeDataString(Text.Trim())}");
        }

        foreach (var tag in TagIds ?? [])
        {
            parts.Add($"tagids[]={tag}");
        }

        if (GameVersionTagId is { } version)
        {
            parts.Add($"gameversions[]={version}");
        }

        parts.Add($"orderby={OrderByParameter(Sort)}");
        parts.Add($"orderdirection={(Ascending ? "asc" : "desc")}");
        return "api/mods?" + string.Join("&", parts);
    }
}

/// <summary>Filters the mod database leaves to the client.</summary>
public sealed record ModDbLocalFilter(bool HideClientOnly = true, bool ModsOnly = true);

public sealed record ModDbPage(IReadOnlyList<ModDbSearchResult> Items, int TotalCount, int Page, int PageSize)
{
    public int PageCount => TotalCount == 0 ? 1 : (TotalCount + PageSize - 1) / PageSize;
}

public static class ModDbBrowsing
{
    /// <summary>Applies the local filters and returns one page (1-based) of the results.</summary>
    public static ModDbPage Page(IReadOnlyList<ModDbSearchResult> results, ModDbLocalFilter filter, int page, int pageSize)
    {
        var filtered = results.Where(r =>
                (!filter.HideClientOnly || !string.Equals(r.Side, "client", StringComparison.OrdinalIgnoreCase)) &&
                (!filter.ModsOnly || r.Type is null || string.Equals(r.Type, "mod", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        pageSize = Math.Max(1, pageSize);
        var pageCount = Math.Max(1, (filtered.Count + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 1, pageCount);
        return new ModDbPage(filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList(), filtered.Count, page, pageSize);
    }
}

public interface IModDbClient
{
    /// <summary>Every mod matching the query, in the requested order (the API doesn't page).</summary>
    Task<IReadOnlyList<ModDbSearchResult>> BrowseAsync(ModDbQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModDbTag>> GetTagsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModDbTag>> GetGameVersionsAsync(CancellationToken cancellationToken = default);

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
