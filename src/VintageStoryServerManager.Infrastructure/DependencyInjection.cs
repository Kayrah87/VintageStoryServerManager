using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VintageStoryServerManager.Core.Backups;
using VintageStoryServerManager.Core.Configuration;
using VintageStoryServerManager.Core.Game;
using VintageStoryServerManager.Core.ModDb;
using VintageStoryServerManager.Core.Server;
using VintageStoryServerManager.Infrastructure.Backups;
using VintageStoryServerManager.Infrastructure.Config;
using VintageStoryServerManager.Infrastructure.Game;
using VintageStoryServerManager.Infrastructure.Logs;
using VintageStoryServerManager.Infrastructure.ModDb;
using VintageStoryServerManager.Infrastructure.Mods;
using VintageStoryServerManager.Infrastructure.Players;
using VintageStoryServerManager.Infrastructure.Server;
using VintageStoryServerManager.Infrastructure.WorldSettings;

namespace VintageStoryServerManager.Infrastructure;

public static class DependencyInjection
{
    private const string UserAgent = "VintageStoryServerManager/1.0 (+https://github.com/Kayrah87/VintageStoryServerManager)";

    /// <summary>Registers the game server supervisor, mods, backups, ModDB, config, logs and players.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DataPathOptions>()
            .Bind(configuration.GetSection(DataPathOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Root), "DataPaths:Root must be set.")
            .ValidateOnStart();
        services.Configure<ServerOptions>(configuration.GetSection(ServerOptions.SectionName));
        services.Configure<BackupOptions>(configuration.GetSection(BackupOptions.SectionName));
        services.Configure<ModDbOptions>(configuration.GetSection(ModDbOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddSingleton<MaintenanceLock>();

        services.AddHttpClient(GameInstallation.HttpClientName, http =>
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.Timeout = TimeSpan.FromMinutes(10);
        });
        services.AddSingleton<IGameInstallation, GameInstallation>();
        services.AddSingleton<IGameServer, GameServerProcess>();
        services.AddHostedService<GameServerHostedService>();

        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<ModLibrary>();

        services.AddHttpClient<IModDbClient, ModDbClient>((sp, http) =>
        {
            http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<ModDbOptions>>().Value.BaseUrl);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddTransient<ModInstaller>();

        services.AddSingleton<ServerConfigService>();
        services.AddSingleton<WorldSettingsService>();
        services.AddSingleton<SettingsApplier>();
        services.AddSingleton<LogService>();
        services.AddSingleton<PlayerService>();

        return services;
    }
}
