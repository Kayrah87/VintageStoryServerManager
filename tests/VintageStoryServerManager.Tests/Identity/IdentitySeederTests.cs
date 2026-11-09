using System.Text.RegularExpressions;
using VintageStoryServerManager.Core.Configuration;
using VintageStoryServerManager.Core.Security;
using VintageStoryServerManager.Web.Data;

namespace VintageStoryServerManager.Tests.Identity;

public sealed class IdentitySeederTests
{
    [Fact]
    public async Task Creates_configured_admin_on_first_run()
    {
        await using var host = new IdentityTestHost(new AdminSeedOptions { Username = "kay", Password = "Sup3r-Secret!" });

        await host.SeedAsync();

        var (exists, isAdmin, passwordOk) = await host.WithUserManagerAsync(async users =>
        {
            var user = await users.FindByNameAsync("kay");
            return (user is not null,
                user is not null && await users.IsInRoleAsync(user, Roles.Admin),
                user is not null && await users.CheckPasswordAsync(user, "Sup3r-Secret!"));
        });
        Assert.True(exists);
        Assert.True(isAdmin);
        Assert.True(passwordOk);
    }

    [Fact]
    public async Task Generates_and_logs_password_when_none_configured()
    {
        await using var host = new IdentityTestHost();

        await host.SeedAsync();

        var line = Assert.Single(host.Logs.Messages, m => m.Contains("generated password"));
        var password = Regex.Match(line, @"generated password: (\S+)").Groups[1].Value;
        var ok = await host.WithUserManagerAsync(async users =>
        {
            var user = await users.FindByNameAsync(AdminSeedOptions.DefaultUsername);
            return user is not null && await users.CheckPasswordAsync(user, password);
        });
        Assert.True(ok);
    }

    [Fact]
    public async Task Does_nothing_when_users_already_exist()
    {
        await using var host = new IdentityTestHost(new AdminSeedOptions { Username = "second", Password = "Sup3r-Secret!" });
        await host.WithUserManagerAsync(users => users.CreateAsync(new ApplicationUser { UserName = "existing" }, "Exist1ng-Pass!"));

        await host.SeedAsync();

        var second = await host.WithUserManagerAsync(users => users.FindByNameAsync("second"));
        Assert.Null(second);
    }

    [Fact]
    public async Task Reset_switch_restores_access_to_existing_admin()
    {
        var seed = new AdminSeedOptions { Username = "kay", Password = "Old-Passw0rd!" };
        await using var host = new IdentityTestHost(seed);
        await host.SeedAsync();
        await host.WithUserManagerAsync(async users =>
        {
            var user = (await users.FindByNameAsync("kay"))!;
            await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddDays(1));
            await users.RemoveFromRoleAsync(user, Roles.Admin);
            return true;
        });

        seed.Password = "New-Passw0rd!";
        seed.ResetPassword = true;
        await host.SeedAsync();

        var (passwordOk, isAdmin, lockedOut) = await host.WithUserManagerAsync(async users =>
        {
            var user = (await users.FindByNameAsync("kay"))!;
            return (await users.CheckPasswordAsync(user, "New-Passw0rd!"),
                await users.IsInRoleAsync(user, Roles.Admin),
                await users.IsLockedOutAsync(user));
        });
        Assert.True(passwordOk);
        Assert.True(isAdmin);
        Assert.False(lockedOut);
    }

    [Fact]
    public async Task Reset_without_password_fails_loudly()
    {
        await using var host = new IdentityTestHost(new AdminSeedOptions { ResetPassword = true });

        await Assert.ThrowsAsync<InvalidOperationException>(host.SeedAsync);
    }

    [Fact]
    public void Generated_passwords_meet_default_rules()
    {
        for (var i = 0; i < 200; i++)
        {
            var password = IdentitySeeder.GeneratePassword();
            Assert.Equal(20, password.Length);
            Assert.Contains(password, char.IsLower);
            Assert.Contains(password, char.IsUpper);
            Assert.Contains(password, char.IsDigit);
            Assert.Contains(password, c => !char.IsLetterOrDigit(c));
        }
    }
}
