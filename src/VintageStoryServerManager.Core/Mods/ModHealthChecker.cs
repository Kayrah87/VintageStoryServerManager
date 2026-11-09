using VintageStoryServerManager.Core.Versioning;

namespace VintageStoryServerManager.Core.Mods;

/// <summary>Finds problems that stop mods from loading: duplicates, missing or outdated dependencies.</summary>
public static class ModHealthChecker
{
    /// <summary>Mod ids provided by the game itself.</summary>
    public static readonly IReadOnlySet<string> BuiltInModIds =
        new HashSet<string>(["game", "creative", "survival"], StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ModIssue> Check(IReadOnlyList<InstalledMod> mods, string? gameVersion)
    {
        var issues = new List<ModIssue>();

        foreach (var mod in mods)
        {
            if (mod.ReadError is not null)
            {
                issues.Add(new(IssueSeverity.Error, $"Can't read mod metadata: {mod.ReadError}", FileName: mod.FileName));
            }
            else if (mod.Info is null)
            {
                issues.Add(mod.Kind switch
                {
                    ModPackageKind.Unknown => new(IssueSeverity.Warning, "Not a mod package. The game ignores this file.", FileName: mod.FileName),
                    ModPackageKind.Assembly or ModPackageKind.SourceFile => new(IssueSeverity.Info, "Code mod without modinfo.json, so its version and dependencies can't be checked.", FileName: mod.FileName),
                    _ => new(IssueSeverity.Warning, "No modinfo.json found, so its version and dependencies can't be checked.", FileName: mod.FileName),
                });
            }
        }

        var enabled = mods.Where(m => m.Enabled && m.Info is not null).ToList();
        var disabledIds = mods.Where(m => !m.Enabled && m.ModId is not null)
            .Select(m => m.ModId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var group in enabled.GroupBy(m => m.ModId!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var files = string.Join(", ", group.Select(m => $"{m.FileName} ({m.Info!.Version ?? "no version"})"));
            foreach (var mod in group)
            {
                issues.Add(new(IssueSeverity.Error, $"'{group.Key}' is installed {group.Count()} times: {files}. Keep only one.", group.Key, mod.FileName));
            }
        }

        var versionComparer = Comparer<string?>.Create(ModVersion.Compare);
        var available = enabled
            .GroupBy(m => m.ModId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Info!.Version).Max(versionComparer), StringComparer.OrdinalIgnoreCase);

        foreach (var mod in enabled)
        {
            foreach (var (depId, required) in mod.Info!.Dependencies)
            {
                if (BuiltInModIds.Contains(depId))
                {
                    if (gameVersion is not null && !ModVersion.Satisfies(gameVersion, required))
                    {
                        issues.Add(new(IssueSeverity.Error,
                            $"Needs game version {required} or newer; the server runs {gameVersion}.", mod.ModId, mod.FileName));
                    }

                    continue;
                }

                if (!available.TryGetValue(depId, out var installedVersion))
                {
                    var why = disabledIds.Contains(depId) ? "is disabled" : "isn't installed";
                    issues.Add(new(IssueSeverity.Error, $"Needs '{depId}'{Describe(required)}, which {why}.", mod.ModId, mod.FileName));
                }
                else if (!ModVersion.Satisfies(installedVersion, required))
                {
                    issues.Add(new(IssueSeverity.Error,
                        $"Needs '{depId}'{Describe(required)}, but {installedVersion ?? "an unknown version"} is installed.", mod.ModId, mod.FileName));
                }
            }

            if (string.Equals(mod.Info.Side, "client", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new(IssueSeverity.Info, "Client-side mod: it does nothing on a server. Players install it themselves.", mod.ModId, mod.FileName));
            }
        }

        return issues;
    }

    private static string Describe(string required) =>
        string.IsNullOrWhiteSpace(required) || required == "*" ? "" : $" {required} or newer";
}
