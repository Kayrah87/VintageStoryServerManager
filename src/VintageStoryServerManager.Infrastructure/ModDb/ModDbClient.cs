using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using VintageStoryServerManager.Core.ModDb;

namespace VintageStoryServerManager.Infrastructure.ModDb;

/// <summary>
/// Client for the public mod database API. The browse endpoint returns the whole catalogue in
/// one response (about 4 MB), so results are cached briefly and paged by the caller.
/// </summary>
public sealed class ModDbClient(HttpClient http, IMemoryCache cache) : IModDbClient
{
    private static readonly TimeSpan BrowseCacheTime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TagCacheTime = TimeSpan.FromHours(6);

    public async Task<IReadOnlyList<ModDbSearchResult>> BrowseAsync(ModDbQuery query, CancellationToken cancellationToken = default)
    {
        var url = query.ToRelativeUrl();
        return await cache.GetOrCreateAsync<IReadOnlyList<ModDbSearchResult>>("moddb:" + url, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = BrowseCacheTime;
            entry.Size = 1;
            var root = await GetJsonAsync(url, cancellationToken);
            return root?["mods"] is JsonArray mods ? mods.OfType<JsonObject>().Select(ReadSearchResult).ToList() : [];
        }) ?? [];
    }

    public Task<IReadOnlyList<ModDbTag>> GetTagsAsync(CancellationToken cancellationToken = default) =>
        GetTagListAsync("api/tags", "tags", cancellationToken);

    public Task<IReadOnlyList<ModDbTag>> GetGameVersionsAsync(CancellationToken cancellationToken = default) =>
        GetTagListAsync("api/gameversions", "gameversions", cancellationToken);

    public async Task<ModDbMod?> GetModAsync(string idOrAlias, CancellationToken cancellationToken = default)
    {
        var root = await GetJsonAsync($"api/mod/{Uri.EscapeDataString(idOrAlias.Trim())}", cancellationToken);
        if (root is null || Str(root["statuscode"]) is { } status && status != "200" || root["mod"] is not JsonObject mod)
        {
            return null;
        }

        var releases = (mod["releases"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(r => Str(r["mainfile"]) is not null)
            .Select(r => new ModDbRelease(
                Int(r["releaseid"]),
                Str(r["modidstr"]) ?? "",
                Str(r["modversion"]) ?? "",
                Str(r["filename"]) ?? $"{Str(r["modidstr"])}_{Str(r["modversion"])}.zip",
                Str(r["mainfile"])!,
                Strings(r["tags"]),
                Str(r["created"])))
            .ToList();

        var screenshots = (mod["screenshots"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(s => Str(s["mainfile"]) is not null)
            .Select(s => new ModDbScreenshot(Str(s["mainfile"])!, Str(s["thumbnailfilename"])))
            .ToList();

        return new ModDbMod(Int(mod["modid"]), Str(mod["name"]) ?? idOrAlias, Str(mod["author"]), Str(mod["urlalias"]), Str(mod["side"]), releases)
        {
            Logo = Str(mod["logofile"]) ?? Str(mod["logofilename"]),
            Screenshots = screenshots,
            Tags = Strings(mod["tags"]),
            Links = new ModDbLinks(
                Url(mod["homepageurl"]), Url(mod["sourcecodeurl"]), Url(mod["issuetrackerurl"]), Url(mod["wikiurl"]), Url(mod["trailervideourl"])),
            Downloads = Int(mod["downloads"]),
            Follows = Int(mod["follows"]),
            Created = Str(mod["created"]),
            LastReleased = Str(mod["lastreleased"]),
        };
    }

    public async Task<Stream> DownloadAsync(ModDbRelease release, CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<ModDbTag>> GetTagListAsync(string url, string property, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync<IReadOnlyList<ModDbTag>>("moddb:" + url, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TagCacheTime;
            entry.Size = 1;
            var root = await GetJsonAsync(url, cancellationToken);
            return root?[property] is JsonArray tags
                ? tags.OfType<JsonObject>()
                    .Select(t => new ModDbTag(Long(t["tagid"]), Str(t["name"]) ?? "", Str(t["color"])))
                    .Where(t => t.Name.Length > 0)
                    .ToList()
                : [];
        }) ?? [];

    private static ModDbSearchResult ReadSearchResult(JsonObject m) => new(
        Int(m["modid"]),
        Str(m["name"]) ?? "",
        Str(m["summary"]),
        Str(m["author"]),
        Strings(m["modidstrs"]),
        Str(m["urlalias"]),
        Str(m["side"]),
        Str(m["type"]),
        Str(m["logo"]),
        Strings(m["tags"]),
        Int(m["downloads"]),
        Int(m["follows"]),
        Int(m["trendingpoints"]),
        Int(m["comments"]),
        Str(m["lastreleased"]));

    private async Task<JsonObject?> GetJsonAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(relativeUrl, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("The mod database returned an unreadable response.", ex);
        }
    }

    private static string? Str(JsonNode? node) => node switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonValue value when value.GetValueKind() == JsonValueKind.Number => value.ToJsonString(),
        _ => null,
    };

    /// <summary>Only http(s) links are passed on, since they end up in href attributes.</summary>
    private static string? Url(JsonNode? node) =>
        Str(node) is { Length: > 0 } text && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? text : null;

    private static int Int(JsonNode? node) =>
        node is JsonValue value && (value.GetValueKind() == JsonValueKind.Number ? value.TryGetValue(out int n) : int.TryParse(Str(node), out n)) ? n : 0;

    private static long Long(JsonNode? node) =>
        node is JsonValue value && (value.GetValueKind() == JsonValueKind.Number ? value.TryGetValue(out long n) : long.TryParse(Str(node), out n)) ? n : 0;

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(Str).OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s)).ToList() : [];
}
