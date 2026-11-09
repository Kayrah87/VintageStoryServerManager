using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VintageStoryServerManager.Core.Security;

namespace VintageStoryServerManager.Web.Data;

public sealed record ManagedUser(string Id, string UserName, bool IsAdmin, bool IsLockedOut);

public sealed record UserAdminResult(bool Succeeded, string? Error = null)
{
    public static readonly UserAdminResult Ok = new(true);

    public static UserAdminResult Fail(string error) => new(false, error);

    public static UserAdminResult From(IdentityResult result) =>
        result.Succeeded ? Ok : Fail(string.Join(" ", result.Errors.Select(e => e.Description)));
}

/// <summary>
/// User management for administrators. Each call runs in its own DI scope so that
/// long-lived interactive components never share a DbContext between operations.
/// </summary>
public sealed class UserAdministrationService(IServiceScopeFactory scopeFactory)
{
    public async Task<IReadOnlyList<ManagedUser>> ListAsync()
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var admins = (await users.GetUsersInRoleAsync(Roles.Admin)).Select(u => u.Id).ToHashSet();
        var all = await users.Users.OrderBy(u => u.UserName).ToListAsync();

        return all
            .Select(u => new ManagedUser(
                u.Id,
                u.UserName ?? "",
                admins.Contains(u.Id),
                u.LockoutEnd is { } end && end > DateTimeOffset.UtcNow))
            .ToList();
    }

    public async Task<UserAdminResult> CreateAsync(string userName, string password, bool isAdmin)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return UserAdminResult.Fail("Username is required.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser { UserName = userName.Trim() };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded || !isAdmin)
        {
            return UserAdminResult.From(created);
        }

        return UserAdminResult.From(await users.AddToRoleAsync(user, Roles.Admin));
    }

    public async Task<UserAdminResult> SetAdminAsync(string userId, bool isAdmin, string actingUserId)
    {
        if (userId == actingUserId)
        {
            return UserAdminResult.Fail("You can't change your own admin role.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await users.FindByIdAsync(userId);
        if (user is null)
        {
            return UserAdminResult.Fail("User not found.");
        }

        var result = isAdmin
            ? await users.AddToRoleAsync(user, Roles.Admin)
            : await users.RemoveFromRoleAsync(user, Roles.Admin);
        if (result.Succeeded)
        {
            // Sign the user out of existing sessions so the role change takes effect.
            await users.UpdateSecurityStampAsync(user);
        }

        return UserAdminResult.From(result);
    }

    public async Task<UserAdminResult> ResetPasswordAsync(string userId, string newPassword)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await users.FindByIdAsync(userId);
        if (user is null)
        {
            return UserAdminResult.Fail("User not found.");
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, newPassword);
        if (result.Succeeded)
        {
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
        }

        return UserAdminResult.From(result);
    }

    public async Task<UserAdminResult> DeleteAsync(string userId, string actingUserId)
    {
        if (userId == actingUserId)
        {
            return UserAdminResult.Fail("You can't delete your own account here.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await users.FindByIdAsync(userId);
        return user is null
            ? UserAdminResult.Fail("User not found.")
            : UserAdminResult.From(await users.DeleteAsync(user));
    }
}
