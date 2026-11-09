using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using VintageStoryServerManager.Core.ModDb;

namespace VintageStoryServerManager.Infrastructure.ModDb;

/// <summary>Client for the public mod database API (<c>/api/mods</c>, <c>/api/mod/{id}</c>).</summary>
public sealed class ModDbClient(HttpClient http) : IModDbClient
{
    public async Task<IReadOnlyList<ModDbSearchResult>> SearchAsync(string text, CancellationToken cancellationToken = default)
    {
        var query = string.IsNullOrWhiteSpace(text) ? "api/mods" : $"api/mods?text={Uri.EscapeDataString(text.Trim())}";
        var root = await GetJsonAsync(query, cancellationToken);
        if (root?["mods"] is not JsonArray mods)
        {
            return [];
        }

        return mods.OfType<JsonObject>().Select(m => new ModDbSearchResult(
            Int(m["modid"]),
            Str(m["name"]) ?? "",
            Str(m["summary"]),
            Str(m["author"]),
            Strings(m["modidstrs"]),
            Str(m["urlalias"]),
            Str(m["side"]),
            Str(m["logo"]),
            Strings(m["tags"]),
            Int(m["downloads"]),
            Str(m["lastreleased"]))).ToList();
    }

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

        return new ModDbMod(Int(mod["modid"]), Str(mod["name"]) ?? idOrAlias, Str(mod["author"]), Str(mod["urlalias"]), Str(mod["side"]), releases);
    }

    public async Task<Stream> DownloadAsync(ModDbRelease release, CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

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

    private static int Int(JsonNode? node) =>
        node is JsonValue value && (value.GetValueKind() == JsonValueKind.Number ? value.TryGetValue(out int n) : int.TryParse(Str(node), out n)) ? n : 0;

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(Str).OfType<string>().ToList() : [];
}
