namespace VintageStoryServerManager.Core.Mods;

/// <summary>Metadata from a mod's <c>modinfo.json</c>.</summary>
public sealed record ModInfo(
    string ModId,
    string Name,
    string? Version,
    string? Type,
    string? Side,
    string? Description,
    IReadOnlyList<string> Authors,
    IReadOnlyDictionary<string, string> Dependencies,
    string? Website);

public enum ModPackageKind
{
    Zip,
    Folder,
    Assembly,
    SourceFile,
    Unknown,
}

/// <summary>A file or folder in the Mods directory (or the manager's disabled-mods folder).</summary>
public sealed record InstalledMod(
    string FileName,
    string FullPath,
    ModPackageKind Kind,
    bool Enabled,
    long SizeBytes,
    ModInfo? Info,
    string? ReadError)
{
    public string DisplayName => Info?.Name ?? FileName;

    /// <summary>Mod id from metadata, or null when the package has none.</summary>
    public string? ModId => Info?.ModId;
}

public enum IssueSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record ModIssue(IssueSeverity Severity, string Message, string? ModId = null, string? FileName = null);

public sealed record ModScanReport(
    IReadOnlyList<InstalledMod> Mods,
    IReadOnlyList<ModIssue> Issues,
    string? GameVersion)
{
    public IEnumerable<ModIssue> IssuesFor(InstalledMod mod) =>
        Issues.Where(i => i.FileName == mod.FileName && (i.ModId is null || i.ModId == mod.ModId));

    public int ErrorCount => Issues.Count(i => i.Severity == IssueSeverity.Error);

    public int WarningCount => Issues.Count(i => i.Severity == IssueSeverity.Warning);
}
