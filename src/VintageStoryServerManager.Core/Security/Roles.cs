namespace VintageStoryServerManager.Core.Security;

/// <summary>
/// Role names used for authorisation in the manager. A signed-in user without a role can
/// view the dashboard and mods.
/// </summary>
public static class Roles
{
    /// <summary>Everything, including mods, backups, server config, game versions and users.</summary>
    public const string Admin = "Admin";

    /// <summary>Day-to-day running: start/stop, console, players and logs.</summary>
    public const string Operator = "Operator";

    /// <summary>For <c>[Authorize(Roles = ...)]</c>: operators and admins.</summary>
    public const string OperatorOrAdmin = Admin + "," + Operator;

    public static readonly IReadOnlyList<string> All = [Admin, Operator];
}
