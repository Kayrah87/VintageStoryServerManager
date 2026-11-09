using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using VintageStoryServerManager.Core.Backups;
using VintageStoryServerManager.Core.Configuration;
using VintageStoryServerManager.Core.Server;
using VintageStoryServerManager.Infrastructure.Backups;
using VintageStoryServerManager.Tests.Support;

namespace VintageStoryServerManager.Tests.Infrastructure;

public sealed class BackupServiceTests : IDisposable
{
    private readonly ModTestContext _ctx = new();

    private DataPathOptions Paths => _ctx.Paths;

    [Fact]
    public async Task Full_backup_round_trips_world_mods_and_config()
    {
        var world = CreateWorld("before");
        TestZips.WriteMod(Paths.ModsDirectory, "a.zip", "a", "1.0.0");
        TestZips.WriteMod(Paths.DisabledModsDirectory, "b.zip", "b", "1.0.0");
        File.WriteAllText(Paths.ServerConfigPath, """{ "ServerName": "before" }""");
        Directory.CreateDirectory(Paths.PlayerDataDirectory);
        File.WriteAllText(Path.Combine(Paths.PlayerDataDirectory, "playerdata.json"), "[]");

        var backup = await _ctx.Backups.CreateAsync(BackupScope.Full, "My backup!", automatic: false);

        Assert.Equal(BackupScope.Full, backup.Scope);
        Assert.Equal("My backup!", backup.Label);
        Assert.False(backup.Automatic);
        Assert.Equal("1.22.7", backup.GameVersion);
        Assert.EndsWith("-full-manual-my-backup.zip", backup.FileName);

        // Change everything, then restore.
        WriteWorldValue(world, "after");
        File.Delete(Path.Combine(Paths.ModsDirectory, "a.zip"));
        TestZips.WriteMod(Paths.ModsDirectory, "c.zip", "c", "1.0.0");
        File.Delete(Path.Combine(Paths.DisabledModsDirectory, "b.zip"));
        File.WriteAllText(Paths.ServerConfigPath, """{ "ServerName": "after" }""");

        await _ctx.Backups.RestoreAsync(backup.FileName);

        Assert.Equal("before", ReadWorldValue(world));
        Assert.True(File.Exists(Path.Combine(Paths.ModsDirectory, "a.zip")));
        Assert.False(File.Exists(Path.Combine(Paths.ModsDirectory, "c.zip")));
        Assert.True(File.Exists(Path.Combine(Paths.DisabledModsDirectory, "b.zip")));
        Assert.Contains("before", File.ReadAllText(Paths.ServerConfigPath));
        Assert.True(File.Exists(Path.Combine(Paths.PlayerDataDirectory, "playerdata.json")));

        // The restore backed up what it replaced.
        var all = await _ctx.Backups.ListAsync();
        Assert.Contains(all, b => b.Automatic && b.Label!.StartsWith("before restoring"));
    }

    [Fact]
    public async Task World_backup_reads_a_database_held_open_in_wal_mode()
    {
        var world = CreateWorld("live");
        using var holder = new SqliteConnection($"Data Source={world};Pooling=False");
        holder.Open();
        using (var wal = holder.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL; UPDATE gamedata SET value = 'in-wal';";
            wal.ExecuteNonQuery();
        }

        var backup = await _ctx.Backups.CreateAsync(BackupScope.World, null, automatic: false);
        holder.Close();

        File.Delete(world);
        await _ctx.Backups.RestoreAsync(backup.FileName);
        Assert.Equal("in-wal", ReadWorldValue(world));
    }

    [Fact]
    public async Task Restore_refuses_while_the_server_runs()
    {
        var backup = await _ctx.Backups.CreateAsync(BackupScope.Config, null, automatic: false);
        _ctx.Server.SetStatus(ServerStatus.Running);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _ctx.Backups.RestoreAsync(backup.FileName));
    }

    [Fact]
    public async Task Prunes_old_automatic_backups_but_keeps_manual_ones()
    {
        var backups = new BackupService(_ctx.Root.Options, Microsoft.Extensions.Options.Options.Create(new BackupOptions { KeepAutomatic = 2 }),
            _ctx.Installation, _ctx.Server, TimeProvider.System, NullLogger<BackupService>.Instance);

        await backups.CreateAsync(BackupScope.Mods, "manual", automatic: false);
        for (var i = 0; i < 4; i++)
        {
            await backups.CreateAsync(BackupScope.Mods, $"auto {i}", automatic: true);
        }

        var all = await backups.ListAsync();
        Assert.Equal(2, all.Count(b => b.Automatic));
        Assert.Single(all, b => !b.Automatic);
        Assert.Contains(all, b => b.Label == "auto 3");
    }

    [Fact]
    public void Rejects_paths_outside_the_backup_folder()
    {
        Assert.Null(_ctx.Backups.GetPath("../manager/manager.db"));
        Assert.Null(_ctx.Backups.GetPath("missing.zip"));
    }

    private string CreateWorld(string value)
    {
        var saves = Path.Combine(Paths.GameDataDirectory, "Saves");
        Directory.CreateDirectory(saves);
        var path = Path.Combine(saves, "default.vcdbs");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE gamedata (value TEXT); INSERT INTO gamedata VALUES ($v);";
        command.Parameters.AddWithValue("$v", value);
        command.ExecuteNonQuery();
        return path;
    }

    private static void WriteWorldValue(string path, string value)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE gamedata SET value = $v";
        command.Parameters.AddWithValue("$v", value);
        command.ExecuteNonQuery();
    }

    private static string? ReadWorldValue(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM gamedata";
        return command.ExecuteScalar() as string;
    }

    public void Dispose() => _ctx.Dispose();
}
