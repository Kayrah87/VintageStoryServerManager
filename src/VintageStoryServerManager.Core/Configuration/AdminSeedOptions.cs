namespace VintageStoryServerManager.Core.Configuration;

/// <summary>
/// The first administrator account, created on startup when the manager has no users.
/// Set with the environment variables <c>Admin__Username</c> and <c>Admin__Password</c>.
/// </summary>
public sealed class AdminSeedOptions
{
    public const string SectionName = "Admin";

    public const string DefaultUsername = "admin";

    /// <summary>Username of the first admin. Defaults to <c>admin</c>.</summary>
    public string? Username { get; set; }

    /// <summary>
    /// Password of the first admin. When empty, a random password is generated and written
    /// once to the log.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Recovery switch: when true, on startup the <see cref="Username"/> account is created
    /// or has its password reset to <see cref="Password"/>, is unlocked, and is made an admin,
    /// even if other users exist. Remove it again once you have signed in.
    /// </summary>
    public bool ResetPassword { get; set; }

    public string EffectiveUsername => string.IsNullOrWhiteSpace(Username) ? DefaultUsername : Username.Trim();
}
