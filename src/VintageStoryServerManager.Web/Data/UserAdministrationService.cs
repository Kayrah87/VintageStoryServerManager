using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VintageStoryServerManager.Core.Security;

namespace VintageStoryServerManager.Web.Data;

/// <param name="Role">The user's manager role (<see cref="Roles.Admin"/> or <see cref="Roles.Operator"/>), or null for a viewer.</param>
public sealed record ManagedUser(string Id, string UserName, string? Role, bool IsLockedOut)
{
    public bool IsAdmin => Role == Roles.Admin;
}

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
        var operators = (await users.GetUsersInRoleAsync(Roles.Operator)).Select(u => u.Id).ToHashSet();
        var all = await users.Users.OrderBy(u => u.UserName).ToListAsync();

        return all
            .Select(u => new ManagedUser(
                u.Id,
                u.UserName ?? "",
                admins.Contains(u.Id) ? Roles.Admin : operators.Contains(u.Id) ? Roles.Operator : null,
                u.LockoutEnd is { } end && end > DateTimeOffset.UtcNow))
            .ToList();
    }

    public async Task<UserAdminResult> CreateAsync(string userName, string password, string? role)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return UserAdminResult.Fail("Username is required.");
        }

        if (role is not null && !Roles.All.Contains(role))
        {
            return UserAdminResult.Fail($"Unknown role '{role}'.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser { UserName = userName.Trim() };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded || role is null)
        {
            return UserAdminResult.From(created);
        }

        return UserAdminResult.From(await users.AddToRoleAsync(user, role));
    }

    /// <summary>Sets the user's role; null makes them a viewer.</summary>
    public async Task<UserAdminResult> SetRoleAsync(string userId, string? role, string actingUserId)
    {
        if (userId == actingUserId)
        {
            return UserAdminResult.Fail("You can't change your own role.");
        }

        if (role is not null && !Roles.All.Contains(role))
        {
            return UserAdminResult.Fail($"Unknown role '{role}'.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await users.FindByIdAsync(userId);
        if (user is null)
        {
            return UserAdminResult.Fail("User not found.");
        }

        var current = (await users.GetRolesAsync(user)).Where(r => Roles.All.Contains(r)).ToList();
        if (current.Count > 0)
        {
            var removed = await users.RemoveFromRolesAsync(user, current);
            if (!removed.Succeeded)
            {
                return UserAdminResult.From(removed);
            }
        }

        if (role is not null)
        {
            var added = await users.AddToRoleAsync(user, role);
            if (!added.Succeeded)
            {
                return UserAdminResult.From(added);
            }
        }

        // Sign the user out of existing sessions so the change takes effect.
        await users.UpdateSecurityStampAsync(user);
        return UserAdminResult.Ok;
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
