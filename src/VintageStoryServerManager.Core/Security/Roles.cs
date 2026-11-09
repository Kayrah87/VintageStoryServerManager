namespace VintageStoryServerManager.Core.Security;

/// <summary>Role names used for authorisation in the manager.</summary>
public static class Roles
{
    /// <summary>Full control, including managing other users.</summary>
    public const string Admin = "Admin";

    public static readonly IReadOnlyList<string> All = [Admin];
}
