using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VintageStoryServerManager.Core.Configuration;
using VintageStoryServerManager.Core.Security;

namespace VintageStoryServerManager.Web.Data;

/// <summary>
/// Creates the roles and, on first run, the initial administrator. Public registration
/// is disabled, so this is the only way the first account comes into existence.
/// </summary>
public sealed class IdentitySeeder(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<AdminSeedOptions> options,
    ILogger<IdentitySeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                ThrowIfFailed(await roleManager.CreateAsync(new IdentityRole(role)), $"create role '{role}'");
            }
        }

        var seed = options.Value;
        if (seed.ResetPassword)
        {
            await ResetAdminAsync(seed);
            return;
        }

        if (await userManager.Users.AnyAsync(cancellationToken))
        {
            return;
        }

        var generated = string.IsNullOrEmpty(seed.Password);
        var password = generated ? GeneratePassword() : seed.Password!;
        var user = new ApplicationUser { UserName = seed.EffectiveUsername };

        ThrowIfFailed(await userManager.CreateAsync(user, password), $"create admin user '{user.UserName}'");
        ThrowIfFailed(await userManager.AddToRoleAsync(user, Roles.Admin), $"add '{user.UserName}' to {Roles.Admin}");

        if (generated)
        {
            logger.LogWarning(
                "Created initial admin account '{Username}' with generated password: {Password}  " +
                "Sign in and change it under your account settings. Set Admin__Password to choose it yourself.",
                user.UserName, password);
        }
        else
        {
            logger.LogInformation("Created initial admin account '{Username}'.", user.UserName);
        }
    }

    private async Task ResetAdminAsync(AdminSeedOptions seed)
    {
        if (string.IsNullOrEmpty(seed.Password))
        {
            throw new InvalidOperationException("Admin__ResetPassword is set but Admin__Password is empty.");
        }

        var user = await userManager.FindByNameAsync(seed.EffectiveUsername);
        if (user is null)
        {
            user = new ApplicationUser { UserName = seed.EffectiveUsername };
            ThrowIfFailed(await userManager.CreateAsync(user, seed.Password), $"create admin user '{user.UserName}'");
        }
        else
        {
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            ThrowIfFailed(await userManager.ResetPasswordAsync(user, token, seed.Password), $"reset password for '{user.UserName}'");
            await userManager.SetLockoutEndDateAsync(user, null);
            await userManager.ResetAccessFailedCountAsync(user);
        }

        if (!await userManager.IsInRoleAsync(user, Roles.Admin))
        {
            ThrowIfFailed(await userManager.AddToRoleAsync(user, Roles.Admin), $"add '{user.UserName}' to {Roles.Admin}");
        }

        logger.LogWarning(
            "Admin__ResetPassword is set: reset the password of admin account '{Username}'. Remove the setting now.",
            user.UserName);
    }

    /// <summary>A random password that satisfies the default Identity password rules.</summary>
    internal static string GeneratePassword()
    {
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string digits = "23456789";
        const string symbols = "!@#$%*-_+=";
        const string all = lower + upper + digits + symbols;

        var chars = new char[20];
        chars[0] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[1] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        chars[3] = symbols[RandomNumberGenerator.GetInt32(symbols.Length)];
        for (var i = 4; i < chars.Length; i++)
        {
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        }

        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }

    private static void ThrowIfFailed(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            var errors = string.Join(" ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Could not {action}: {errors}");
        }
    }
}
