using MudBlazor;
using VintageStoryServerManager.Core.Backups;
using VintageStoryServerManager.Core.Mods;
using VintageStoryServerManager.Core.Server;

namespace VintageStoryServerManager.Web.Components.Shared;

/// <summary>Labels and colours shared by the pages.</summary>
public static class ServerDisplay
{
    public static Color StatusColor(ServerStatus status) => status switch
    {
        ServerStatus.Running => Color.Success,
        ServerStatus.Starting or ServerStatus.Stopping => Color.Warning,
        ServerStatus.Crashed => Color.Error,
        _ => Color.Default,
    };

    public static string StatusLabel(ServerStatus status) => status switch
    {
        ServerStatus.Running => "Running",
        ServerStatus.Starting => "Starting",
        ServerStatus.Stopping => "Stopping",
        ServerStatus.Crashed => "Crashed",
        _ => "Stopped",
    };

    public static Color SeverityColor(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Error => Color.Error,
        IssueSeverity.Warning => Color.Warning,
        _ => Color.Info,
    };

    public static Severity SnackbarSeverity(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Error => Severity.Error,
        IssueSeverity.Warning => Severity.Warning,
        _ => Severity.Info,
    };

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B",
    };

    public static string Duration(TimeSpan span) => span.TotalDays >= 1
        ? $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{span.Minutes}m {span.Seconds}s";

    public static string Scope(BackupScope scope) =>
        scope == BackupScope.Full ? "Full" : scope.ToString().Replace(", ", " + ");
}
