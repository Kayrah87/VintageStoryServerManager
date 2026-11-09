using Microsoft.Extensions.DependencyInjection;
using VintageStoryServerManager.Core.Configuration;
using VintageStoryServerManager.Web.Data;

namespace VintageStoryServerManager.Tests.Identity;

public sealed class UserAdministrationServiceTests
{
    private const string Password = "Val1d-Password!";

    private static async Task<(IdentityTestHost Host, UserAdministrationService Admin, string AdminId)> CreateAsync()
    {
        var host = new IdentityTestHost(new AdminSeedOptions { Username = "admin", Password = Password });
        await host.SeedAsync();
        var admin = host.Services.GetRequiredService<UserAdministrationService>();
        var adminId = (await admin.ListAsync()).Single().Id;
        return (host, admin, adminId);
    }

    [Fact]
    public async Task Creates_users_with_and_without_admin_role()
    {
        var (host, admin, _) = await CreateAsync();
        await using var _host = host;

        Assert.True((await admin.CreateAsync("player", Password, isAdmin: false)).Succeeded);
        Assert.True((await admin.CreateAsync("helper", Password, isAdmin: true)).Succeeded);

        var users = await admin.ListAsync();
        Assert.False(users.Single(u => u.UserName == "player").IsAdmin);
        Assert.True(users.Single(u => u.UserName == "helper").IsAdmin);
    }

    [Fact]
    public async Task Rejects_weak_password_and_duplicate_username()
    {
        var (host, admin, _) = await CreateAsync();
        await using var _host = host;

        var weak = await admin.CreateAsync("player", "short", isAdmin: false);
        var duplicate = await admin.CreateAsync("admin", Password, isAdmin: false);

        Assert.False(weak.Succeeded);
        Assert.False(duplicate.Succeeded);
        Assert.Single(await admin.ListAsync());
    }

    [Fact]
    public async Task Admin_cannot_demote_or_delete_themselves()
    {
        var (host, admin, adminId) = await CreateAsync();
        await using var _host = host;

        Assert.False((await admin.SetAdminAsync(adminId, false, adminId)).Succeeded);
        Assert.False((await admin.DeleteAsync(adminId, adminId)).Succeeded);
        Assert.True((await admin.ListAsync()).Single().IsAdmin);
    }

    [Fact]
    public async Task Toggles_admin_role_and_deletes_other_users()
    {
        var (host, admin, adminId) = await CreateAsync();
        await using var _host = host;
        await admin.CreateAsync("player", Password, isAdmin: false);
        var playerId = (await admin.ListAsync()).Single(u => u.UserName == "player").Id;

        Assert.True((await admin.SetAdminAsync(playerId, true, adminId)).Succeeded);
        Assert.True((await admin.ListAsync()).Single(u => u.Id == playerId).IsAdmin);

        Assert.True((await admin.DeleteAsync(playerId, adminId)).Succeeded);
        Assert.DoesNotContain(await admin.ListAsync(), u => u.Id == playerId);
    }

    [Fact]
    public async Task Resets_password()
    {
        var (host, admin, _) = await CreateAsync();
        await using var _host = host;
        await admin.CreateAsync("player", Password, isAdmin: false);
        var playerId = (await admin.ListAsync()).Single(u => u.UserName == "player").Id;

        Assert.True((await admin.ResetPasswordAsync(playerId, "An0ther-Password!")).Succeeded);

        var ok = await host.WithUserManagerAsync(async users =>
            await users.CheckPasswordAsync((await users.FindByIdAsync(playerId))!, "An0ther-Password!"));
        Assert.True(ok);
    }
}
