using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using VintageStoryServerManager.Core.Backups;
using VintageStoryServerManager.Core.Configuration;
using VintageStoryServerManager.Core.Game;
using VintageStoryServerManager.Core.Server;
using VintageStoryServerManager.Infrastructure.Config;
using VintageStoryServerManager.Infrastructure.Game;
using VintageStoryServerManager.Infrastructure.Logs;
using VintageStoryServerManager.Infrastructure.Players;
using VintageStoryServerManager.Tests.Support;

namespace VintageStoryServerManager.Tests.Infrastructure;

public sealed class GameInstallationTests : IDisposable
{
    private readonly TempDataRoot _root = new();

    [Fact]
    public void Uses_the_bundled_install_until_another_version_is_activated()
    {
        WriteInstall(_root.Paths.GameInstallDirectory, "1.22.7");
        WriteInstall(Path.Combine(_root.Paths.GameVersionsDirectory, "1.22.6"), "1.22.6");
        var installation = Create(new StubHandler());

        Assert.Equal("1.22.7", installation.ActiveVersion);
        Assert.Equal(_root.Paths.GameInstallDirectory, installation.ActiveDirectory);

        installation.Activate("1.22.6");
        Assert.Equal("1.22.6", installation.ActiveVersion);
        Assert.Throws<InvalidOperationException>(() => installation.Remove("1.22.6"));

        installation.Activate("1.22.7");
        Assert.Equal(_root.Paths.GameInstallDirectory, installation.ActiveDirectory);
        Assert.Throws<InvalidOperationException>(() => installation.Remove("1.22.7"));

        installation.Remove("1.22.6");
        Assert.Single(installation.ListInstalled());
    }

    [Fact]
    public async Task Lists_releases_and_installs_a_verified_download()
    {
        var archive = ServerTarball();
        var md5 = Convert.ToHexStringLower(MD5.HashData(archive));
        var handler = new StubHandler
        {
            ["https://api.test/stable.json"] = """
                {"1.22.8":{"linuxserver":{"filename":"vs_server_linux-x64_1.22.8.tar.gz","md5":"MD5","urls":{"cdn":"https://cdn.test/1.22.8.tar.gz"},"latest":1}},
                 "1.22.7":{"linuxserver":{"filename":"x","md5":"bad","urls":{"cdn":"https://cdn.test/1.22.7.tar.gz"},"latest":0}}}
                """.Replace("MD5", md5),
            ["https://api.test/unstable.json"] = "{}",
            ["https://cdn.test/1.22.8.tar.gz"] = archive,
            ["https://cdn.test/1.22.7.tar.gz"] = archive,
        };
        var installation = Create(handler);

        var releases = await installation.GetAvailableAsync();
        Assert.Equal(["1.22.8", "1.22.7"], releases.Select(r => r.Version));
        Assert.True(releases[0].Latest);

        await installation.InstallAsync(releases[0]);
        var installed = Assert.Single(installation.ListInstalled());
        Assert.Equal("1.22.8", installed.Version);
        Assert.False(installed.IsBundled);

        await Assert.ThrowsAsync<InvalidDataException>(() => installation.InstallAsync(releases[1]));
        Assert.Single(installation.ListInstalled());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root.Paths.TempDirectory));
    }

    private GameInstallation Create(StubHandler handler) =>
        new(_root.Options, Microsoft.Extensions.Options.Options.Create(new ModDbOptions { GameApiUrl = "https://api.test/" }),
            new StubFactory(handler), NullLogger<GameInstallation>.Instance);

    private static void WriteInstall(string dir, string version)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, GameInstallation.ServerAssembly), "");
        File.WriteAllText(Path.Combine(dir, GameInstallation.VersionFile), version);
    }

    private static byte[] ServerTarball()
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, GameInstallation.ServerAssembly) { DataStream = new MemoryStream("dll"u8.ToArray()) };
            tar.WriteEntry(entry);
        }

        return buffer.ToArray();
    }

    public void Dispose() => _root.Dispose();

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _responses = new();

        public object this[string url]
        {
            set => _responses[url] = value is string text ? System.Text.Encoding.UTF8.GetBytes(text) : (byte[])value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responses.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public sealed class PlayerServiceTests : IDisposable
{
    private readonly TempDataRoot _root = new();
    private readonly FakeGameServer _server = new();

    [Fact]
    public void Reads_the_servers_player_files()
    {
        var dir = _root.Paths.PlayerDataDirectory;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "playerdata.json"), """[{"PlayerUID":"u1","RoleCode":"admin","LastKnownPlayername":"Kay"}]""");
        File.WriteAllText(Path.Combine(dir, "playersbanned.json"),
            """[{"PlayerUID":"u2","PlayerName":"Griefer","UntilDate":"2027-11-09T09:45:42+00:00","Reason":"griefing","IssuedByPlayerName":"Console Admin"}]""");
        File.WriteAllText(Path.Combine(dir, "playerswhitelisted.json"), """[{"PlayerUID":"u1","PlayerName":"Kay","UntilDate":"2076-11-09T09:44:21+00:00"}]""");
        File.WriteAllText(_root.Paths.ServerConfigPath, """{"Roles":[{"Code":"suvisitor"},{"Code":"suplayer"},{"Code":"admin"}],"DefaultRoleCode":"suplayer","WhitelistMode":1}""");

        var lists = new PlayerService(_root.Options, _server).Read();

        Assert.Equal("admin", Assert.Single(lists.Known).Role);
        Assert.Equal("griefing", Assert.Single(lists.Banned).Reason);
        Assert.Equal("Kay", Assert.Single(lists.Whitelisted).Name);
        Assert.Equal(["suvisitor", "suplayer", "admin"], lists.Roles);
        Assert.Equal("suplayer", lists.DefaultRole);
        Assert.Equal(1, lists.WhitelistMode);
    }

    [Fact]
    public async Task Sends_the_commands_the_server_understands()
    {
        _server.SetStatus(ServerStatus.Running);
        var players = new PlayerService(_root.Options, _server);

        await players.KickAsync("Kay", null);
        await players.BanAsync("Griefer", 2, BanUnit.Week, "griefing\nagain");
        await players.UnbanAsync("Griefer");
        await players.SetWhitelistedAsync("Kay", true);
        await players.SetRoleAsync("Kay", "admin");

        Assert.Equal(
            [
                "/kick Kay Kicked by an admin",
                "/ban Griefer 2 week griefing again",
                "/unban Griefer",
                "/player Kay whitelist on",
                "/player Kay role admin",
            ],
            _server.Commands);
    }

    [Fact]
    public async Task Rejects_names_that_could_inject_commands()
    {
        _server.SetStatus(ServerStatus.Running);
        var players = new PlayerService(_root.Options, _server);

        await Assert.ThrowsAsync<ArgumentException>(() => players.KickAsync("Kay /stop", null));
        Assert.Empty(_server.Commands);
    }

    public void Dispose() => _root.Dispose();
}

public sealed class ServerConfigAndLogTests : IDisposable
{
    private readonly ModTestContext _ctx = new();

    private ServerConfigService Config => new(_ctx.Root.Options, _ctx.Server, _ctx.Backups, _ctx.Lock, NullLogger<ServerConfigService>.Instance);

    [Fact]
    public async Task Saves_valid_config_and_backs_up_the_old_one()
    {
        File.WriteAllText(_ctx.Paths.ServerConfigPath, """{ "ServerName": "old" }""");

        await Config.SaveAsync("""{ "ServerName": "new" }""");

        Assert.Contains("new", File.ReadAllText(_ctx.Paths.ServerConfigPath));
        var backup = Assert.Single(await _ctx.Backups.ListAsync());
        Assert.Equal(BackupScope.Config, backup.Scope);
    }

    [Fact]
    public async Task Refuses_invalid_json_and_edits_while_running()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Config.SaveAsync("{ nope"));
        Assert.NotNull(ServerConfigService.Validate("[1, 2]"));

        _ctx.Server.SetStatus(ServerStatus.Running);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Config.SaveAsync("{}"));
    }

    [Fact]
    public async Task Tails_logs_and_lists_crash_logs_first()
    {
        var logs = _ctx.Paths.LogsDirectory;
        Directory.CreateDirectory(Path.Combine(logs, "Archive"));
        File.WriteAllLines(Path.Combine(logs, "server-main.log"), Enumerable.Range(1, 3000).Select(i => $"line {i}"));
        File.WriteAllText(Path.Combine(logs, "Archive", "server-crash.log"), "boom");
        var service = new LogService(_ctx.Root.Options);

        var files = service.List();
        Assert.Equal("Archive/server-crash.log", files[0].Name);

        var tail = await service.TailAsync("server-main.log", 100);
        Assert.Equal(100, tail.Count);
        Assert.Equal("line 3000", tail[^1]);

        Assert.Null(service.GetPath("../serverconfig.json"));
    }

    public void Dispose() => _ctx.Dispose();
}
