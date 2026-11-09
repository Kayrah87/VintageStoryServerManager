using System.Text.Json.Nodes;

namespace VintageStoryServerManager.Core.Configuration;

/// <summary>
/// Repairs absolute paths in a serverconfig.json that was written by another container or host.
/// The game stores the world location (WorldConfig.SaveFileLocation) and mod folders (ModPaths)
/// as absolute paths, so data from e.g. an image that used /vintagestory/data points at folders
/// that don't exist here, and the server refuses to start.
/// </summary>
public static class ImportedDataPaths
{
    /// <summary>
    /// Rewrites paths under the old data folder to <paramref name="dataDirectory"/> when the world
    /// file is missing at its recorded location but present at the same place under
    /// <paramref name="dataDirectory"/>. Returns a description of each change; empty if none.
    /// </summary>
    public static IReadOnlyList<string> Fix(JsonObject config, string dataDirectory, Func<string, bool> fileExists)
    {
        var dataRoot = dataDirectory.TrimEnd('/');
        if (FindOldRoot(config, dataRoot, fileExists) is not { } oldRoot)
        {
            return [];
        }

        var changes = new List<string>();
        Rewrite(config, oldRoot, dataRoot, changes);
        return changes;
    }

    /// <summary>The data folder the config was written for, if it differs from this one.</summary>
    private static string? FindOldRoot(JsonObject config, string dataRoot, Func<string, bool> fileExists)
    {
        if (config["WorldConfig"] is not JsonObject world ||
            world["SaveFileLocation"] is not JsonValue location ||
            !location.TryGetValue<string>(out var save) ||
            !save.StartsWith('/') ||
            IsUnder(save, dataRoot) ||
            fileExists(save))
        {
            return null;
        }

        var saves = save.LastIndexOf("/Saves/", StringComparison.Ordinal);
        if (saves <= 0 || !fileExists(dataRoot + save[saves..]))
        {
            return null;
        }

        return save[..saves];
    }

    private static void Rewrite(JsonNode? node, string oldRoot, string dataRoot, List<string> changes)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (Replacement(child, oldRoot, dataRoot) is { } updated)
                    {
                        changes.Add($"{key}: {child} → {updated}");
                        obj[key] = updated;
                    }
                    else
                    {
                        Rewrite(child, oldRoot, dataRoot, changes);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (Replacement(array[i], oldRoot, dataRoot) is { } updated)
                    {
                        changes.Add($"{array.GetPath().Split('.').Last()}: {array[i]} → {updated}");
                        array[i] = updated;
                    }
                    else
                    {
                        Rewrite(array[i], oldRoot, dataRoot, changes);
                    }
                }

                break;
        }
    }

    private static string? Replacement(JsonNode? node, string oldRoot, string dataRoot) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && IsUnder(text, oldRoot)
            ? dataRoot + text[oldRoot.Length..]
            : null;

    private static bool IsUnder(string path, string root) =>
        path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
}
