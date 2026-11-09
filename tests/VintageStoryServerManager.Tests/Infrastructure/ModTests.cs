using Microsoft.Extensions.Logging.Abstractions;
using VintageStoryServerManager.Core.Backups;
using VintageStoryServerManager.Core.Mods;
using VintageStoryServerManager.Infrastructure.ModDb;
using VintageStoryServerManager.Tests.Support;

namespace VintageStoryServerManager.Tests.Infrastructure;

public sealed class ModLibraryTests : IDisposable
{
    private readonly ModTestContext _ctx = new();

    [Fact]
    public void Scans_enabled_disabled_and_unreadable_mods()
    {
        TestZips.WriteMod(_ctx.Paths.ModsDirectory, "exlib_0.8.4.zip", "exlib", "0.8.4");
        TestZips.WriteMod(_ctx.Paths.DisabledModsDirectory, "old.zip", "old", "1.0.0");
        File.WriteAllText(Path.Combine(_ctx.Paths.ModsDirectory, "broken.zip"), "not a zip");

        var report = _ctx.Library.Scan();

        Assert.Equal("1.22.7", report.GameVersion);
        Assert.Contains(report.Mods, m => m.ModId == "exlib" && m.Enabled && m.Info!.Version == "0.8.4");
        Assert.Contains(report.Mods, m => m.ModId == "old" && !m.Enabled);
        var broken = Assert.Single(report.Mods, m => m.FileName == "broken.zip");
        Assert.NotNull(broken.ReadError);
        Assert.Contains(report.Issues, i => i.FileName == "broken.zip" && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public async Task Disable_and_enable_move_the_file_and_back_up_first()
    {
        TestZips.WriteMod(_ctx.Paths.ModsDirectory, "a.zip", "a", "1.0.0");

        await _ctx.Library.DisableAsync("a.zip");
        Assert.True(File.Exists(Path.Combine(_ctx.Paths.DisabledModsDirectory, "a.zip")));
        Assert.False(File.Exists(Path.Combine(_ctx.Paths.ModsDirectory, "a.zip")));

        await _ctx.Library.EnableAsync("a.zip");
        Assert.True(File.Exists(Path.Combine(_ctx.Paths.ModsDirectory, "a.zip")));

        var backups = await _ctx.Backups.ListAsync();
        Assert.Equal(2, backups.Count(b => b.Automatic && b.Scope == BackupScope.Mods));
    }

    [Fact]
    public async Task Delete_removes_the_mod()
    {
        TestZips.WriteMod(_ctx.Paths.ModsDirectory, "a.zip", "a", "1.0.0");

        await _ctx.Library.DeleteAsync("a.zip", enabled: true);

        Assert.Empty(_ctx.Library.Scan().Mods);
    }

    [Fact]
    public async Task Rejects_file_names_that_escape_the_mods_folder() =>
        await Assert.ThrowsAsync<ArgumentException>(() => _ctx.Library.DeleteAsync("../manager/manager.db", enabled: true));

    [Fact]
    public async Task Upload_replaces_every_other_copy_of_the_same_mod()
    {
        TestZips.WriteMod(_ctx.Paths.ModsDirectory, "exlib_0.7.2.zip", "exlib", "0.7.2");
        TestZips.WriteMod(_ctx.Paths.DisabledModsDirectory, "exlib-old.zip", "exlib", "0.6.0");

        var info = await _ctx.Library.InstallUploadAsync(new MemoryStream(TestZips.Mod("exlib", "0.8.4")), "exlib_0.8.4.zip");

        Assert.Equal("0.8.4", info.Version);
        var exlib = Assert.Single(_ctx.Library.Scan().Mods, m => m.ModId == "exlib");
        Assert.Equal("exlib_0.8.4.zip", exlib.FileName);
    }

    [Fact]
    public async Task Upload_rejects_non_mods()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => _ctx.Library.InstallUploadAsync(new MemoryStream([1, 2, 3]), "x.zip"));
        await Assert.ThrowsAsync<InvalidDataException>(() => _ctx.Library.InstallUploadAsync(new MemoryStream([1]), "x.dll"));
        Assert.Empty(_ctx.Library.Scan().Mods);
    }

    public void Dispose() => _ctx.Dispose();
}

public sealed class ModInstallerTests : IDisposable
{
    private readonly ModTestContext _ctx = new();
    private readonly FakeModDb _modDb = new();

    private ModInstaller Installer =>
        new(_modDb, _ctx.Library, _ctx.Backups, _ctx.Lock, _ctx.Root.Options, _cache, NullLogger<ModInstaller>.Instance);

    private readonly Microsoft.Extensions.Caching.Memory.MemoryCache _cache = new(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

    [Fact]
    public async Task Installs_a_mod_with_its_missing_dependencies()
    {
        _modDb
            .Add("exlib", "0.7.2", ["1.22.6"])
            .Add("exlib", "0.8.4", ["1.22.7"])
            .Add("ppex", "0.7.1", ["1.22.7"], new() { ["exlib"] = "0.8.4", ["game"] = "1.22.0" });

        var plan = await Installer.PlanAsync("ppex");

        Assert.Empty(plan.Errors);
        Assert.Equal(["ppex", "exlib"], plan.Steps.Select(s => s.ModId));
        Assert.Equal("0.8.4", plan.Steps[1].Release.ModVersion);
        Assert.Equal("Needed by ppex mod", plan.Steps[1].Reason);

        var backup = await Installer.ApplyAsync(plan);

        Assert.Equal(BackupScope.Mods, backup.Scope);
        var report = _ctx.Library.Scan();
        Assert.Equal(["exlib", "ppex"], report.Mods.Select(m => m.ModId).Order());
        Assert.DoesNotContain(report.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.Empty(Directory.EnumerateFiles(_ctx.Paths.TempDirectory));
    }

    [Fact]
    public async Task Updates_an_outdated_dependency_and_replaces_the_old_file()
    {
        TestZips.WriteMod(_ctx.Paths.ModsDirectory, "exlib_0.7.2.zip", "exlib", "0.7.2");
        _modDb
            .Add("exlib", "0.8.4", ["1.22.7"])
            .Add("ppex", "0.7.1", ["1.22.7"], new() { ["exlib"] = "0.8.4" });

        var plan = await Installer.PlanAsync("ppex");
        Assert.Equal("0.7.2", plan.Steps.Single(s => s.ModId == "exlib").ReplacesVersion);

        await Installer.ApplyAsync(plan);

        var exlib = Assert.Single(_ctx.Library.Scan().Mods, m => m.ModId == "exlib");
        Assert.Equal("0.8.4", exlib.Info!.Version);
    }

    [Fact]
    public async Task Skips_dependencies_that_are_already_satisfied()
    {
        TestZips.WriteMod(_ctx.Paths.ModsDirectory, "exlib.zip", "exlib", "0.9.0");
        _modDb
            .Add("exlib", "0.9.0", ["1.22.7"])
            .Add("ppex", "0.7.1", ["1.22.7"], new() { ["exlib"] = "0.8.4" });

        var plan = await Installer.PlanAsync("ppex");

        Assert.Equal(["ppex"], plan.Steps.Select(s => s.ModId));
        Installer.Discard(plan);
    }

    [Fact]
    public async Task Reports_missing_dependencies_and_incompatible_games()
    {
        _modDb
            .Add("needsghost", "1.0.0", ["1.22.7"], new() { ["ghost"] = "1.0.0" })
            .Add("future", "1.0.0", ["1.23.0"]);

        var ghost = await Installer.PlanAsync("needsghost");
        var future = await Installer.PlanAsync("future");
        var unknown = await Installer.PlanAsync("doesnotexist");

        Assert.Contains(ghost.Errors, e => e.Contains("'ghost'"));
        Assert.False(ghost.CanApply);
        Assert.Contains(future.Errors, e => e.Contains("no release for game version 1.22.7"));
        Assert.Contains(unknown.Errors, e => e.Contains("not found"));
        Installer.Discard(ghost);
    }

    [Fact]
    public async Task Up_to_date_mod_is_a_no_op()
    {
        TestZips.WriteMod(_ctx.Paths.ModsDirectory, "exlib.zip", "exlib", "0.8.4");
        _modDb.Add("exlib", "0.8.4", ["1.22.7"]);

        var plan = await Installer.PlanAsync("exlib");

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Warnings, w => w.Contains("up to date"));
        Assert.Equal(0, _modDb.Downloads);
    }

    [Fact]
    public async Task Finds_updates_for_the_current_game_version()
    {
        TestZips.WriteMod(_ctx.Paths.ModsDirectory, "exlib.zip", "exlib", "0.7.2");
        TestZips.WriteMod(_ctx.Paths.ModsDirectory, "current.zip", "current", "2.0.0");
        _modDb
            .Add("exlib", "0.8.4", ["1.22.7"])
            .Add("exlib", "0.9.0", ["1.23.0"])
            .Add("current", "2.0.0", ["1.22.7"]);

        var updates = await Installer.CheckUpdatesAsync();

        var update = Assert.Single(updates);
        Assert.Equal("exlib", update.ModId);
        Assert.Equal("0.8.4", update.AvailableVersion);
    }

    [Fact]
    public async Task Reuses_an_update_check_until_the_mods_change_or_a_refresh_is_asked_for()
    {
        TestZips.WriteMod(_ctx.Paths.ModsDirectory, "exlib.zip", "exlib", "0.7.2");
        _modDb.Add("exlib", "0.8.4", ["1.22.7"]);
        Assert.Single(await Installer.CheckUpdatesAsync());

        _modDb.Add("exlib", "0.8.5", ["1.22.7"]);
        Assert.Equal("0.8.4", Assert.Single(await Installer.CheckUpdatesAsync()).AvailableVersion);
        Assert.Equal("0.8.5", Assert.Single(await Installer.CheckUpdatesAsync(refresh: true)).AvailableVersion);

        TestZips.WriteMod(_ctx.Paths.ModsDirectory, "exlib.zip", "exlib", "0.8.5");
        Assert.Empty(await Installer.CheckUpdatesAsync());
    }

    [Fact]
    public async Task Updates_several_mods_in_one_plan()
    {
        TestZips.WriteMod(_ctx.Paths.ModsDirectory, "exlib.zip", "exlib", "0.7.2");
        TestZips.WriteMod(_ctx.Paths.ModsDirectory, "other.zip", "other", "1.0.0");
        _modDb
            .Add("exlib", "0.8.4", ["1.22.7"])
            .Add("other", "1.1.0", ["1.22.7"]);

        var updates = await Installer.CheckUpdatesAsync();
        var plan = await Installer.PlanAsync(updates.Select(u => u.ModId).ToList());

        Assert.True(plan.CanApply);
        Assert.Equal(["exlib", "other"], plan.Steps.Select(s => s.ModId).Order());
        Assert.All(plan.Steps, s => Assert.Equal("Update", s.Reason));
        await Installer.ApplyAsync(plan);

        Assert.Empty(await Installer.CheckUpdatesAsync());
        Assert.Single(await _ctx.Backups.ListAsync());
    }

    public void Dispose() => _ctx.Dispose();
}
