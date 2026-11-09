using System.Text.Json.Nodes;

namespace VintageStoryServerManager.Core.Configuration;

/// <summary>
/// Repairs paths in a serverconfig.json that was written by another container or host.
/// The game stores the world location (WorldConfig.SaveFileLocation) and mod folders (ModPaths)
/// as paths relative to wherever the old server ran (e.g. /vintagestory/data/Saves/default.vcdbs,
/// or ./data/Saves/default.vcdbs for a server started with --dataPath ./data). Here they point
/// at folders that don't exist, and the server refuses to start.
/// </summary>
public static class ImportedDataPaths
{
    /// <summary>
    /// Rewrites paths under the old data folder to <paramref name="dataDirectory"/> when the world
    /// file is missing at its recorded location but present at the same place under
    /// <paramref name="dataDirectory"/>. Relative paths are resolved against
    /// <paramref name="workingDirectory"/>, where the game runs. Returns a description of each
    /// change; empty if none.
    /// </summary>
    public static IReadOnlyList<string> Fix(JsonObject config, string dataDirectory, string workingDirectory, Func<string, bool> fileExists)
    {
        var dataRoot = dataDirectory.TrimEnd('/');
        if (config["WorldConfig"] is not JsonObject world ||
            world["SaveFileLocation"] is not JsonValue location ||
            !location.TryGetValue<string>(out var save) ||
            FindOldRoot(save, dataRoot, workingDirectory, fileExists) is not { } oldRoot)
        {
            return [];
        }

        var changes = new List<string>();
        if (oldRoot.Length == 0)
        {
            // "Saves/default.vcdbs": only the world path can be told apart; relative ModPaths
            // such as "Mods" mean the game's own folder and must stay as they are.
            var updated = dataRoot + "/" + save;
            changes.Add($"SaveFileLocation: \"{save}\" → \"{updated}\"");
            world["SaveFileLocation"] = updated;
            return changes;
        }

        Rewrite(config, oldRoot, dataRoot, changes);
        return changes;
    }

    /// <summary>
    /// The data folder the world path was written for ("/vintagestory/data", "./data", or "" for a
    /// path that starts at Saves/), or null when the path already works here.
    /// </summary>
    private static string? FindOldRoot(string save, string dataRoot, string workingDirectory, Func<string, bool> fileExists)
    {
        var resolved = Path.GetFullPath(save, workingDirectory);
        if (IsUnder(resolved, dataRoot) || fileExists(resolved))
        {
            return null;
        }

        var saves = save.StartsWith("Saves/", StringComparison.Ordinal)
            ? 0
            : save.LastIndexOf("/Saves/", StringComparison.Ordinal);
        if (saves < 0)
        {
            return null;
        }

        var rest = saves == 0 ? "/" + save : save[saves..];
        return fileExists(dataRoot + rest) ? save[..saves] : null;
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
