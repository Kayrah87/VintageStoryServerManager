using System.IO.Compression;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VintageStoryServerManager.Core.Mods;

/// <summary>
/// Reads <c>modinfo.json</c>. Parsed with Newtonsoft's lenient reader, like the game itself,
/// so unquoted keys, comments and trailing commas are accepted.
/// </summary>
public static partial class ModInfoReader
{
    public const string FileName = "modinfo.json";

    /// <summary>Reads modinfo.json from the root of a mod zip. Returns null when there is none.</summary>
    public static ModInfo? ReadFromZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return ReadFromZip(zip);
    }

    public static ModInfo? ReadFromZip(ZipArchive zip)
    {
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, FileName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        using var reader = new StreamReader(entry.Open());
        return Parse(reader.ReadToEnd());
    }

    public static ModInfo? ReadFromFolder(string folderPath)
    {
        var file = Directory.EnumerateFiles(folderPath)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), FileName, StringComparison.OrdinalIgnoreCase));
        return file is null ? null : Parse(File.ReadAllText(file));
    }

    public static ModInfo Parse(string json)
    {
        JObject root;
        try
        {
            root = JObject.Parse(json, new JsonLoadSettings
            {
                CommentHandling = CommentHandling.Ignore,
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Replace,
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"modinfo.json is not valid JSON: {ex.Message}", ex);
        }

        var name = Get(root, "name");
        var modId = Get(root, "modid") ?? (name is null ? null : DeriveModId(name));
        if (string.IsNullOrWhiteSpace(modId))
        {
            throw new InvalidDataException("modinfo.json has neither a modid nor a name.");
        }

        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Find(root, "dependencies") is JObject deps)
        {
            foreach (var property in deps.Properties())
            {
                dependencies[property.Name.Trim()] = property.Value.Type == JTokenType.Null ? "" : property.Value.ToString().Trim();
            }
        }

        var authors = Find(root, "authors") switch
        {
            JArray array => array.Select(a => a.ToString()).Where(a => a.Length > 0).ToList(),
            JValue { Type: JTokenType.String } value => [value.ToString()],
            _ => Get(root, "author") is { } single ? [single] : [],
        };

        return new ModInfo(
            modId.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(name) ? modId : name.Trim(),
            Get(root, "version")?.Trim(),
            Get(root, "type"),
            Get(root, "side"),
            Get(root, "description"),
            authors,
            dependencies,
            Get(root, "website"));
    }

    /// <summary>The game derives a missing mod id from the name: lower case, letters and digits only.</summary>
    public static string DeriveModId(string name) => NonAlphanumeric().Replace(name, "").ToLowerInvariant();

    private static JToken? Find(JObject root, string key) =>
        root.Properties().FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? Get(JObject root, string key) =>
        Find(root, key) is { Type: not JTokenType.Null and not JTokenType.Object and not JTokenType.Array } token
            ? token.ToString()
            : null;

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();
}
