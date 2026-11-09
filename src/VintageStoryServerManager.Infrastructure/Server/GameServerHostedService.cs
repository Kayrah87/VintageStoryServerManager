using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VintageStoryServerManager.Core.Configuration;
using VintageStoryServerManager.Core.Server;

namespace VintageStoryServerManager.Infrastructure.Server;

/// <summary>Starts the game server with the app (when configured) and stops it cleanly on shutdown.</summary>
public sealed class GameServerHostedService(
    IGameServer server,
    IOptions<ServerOptions> options,
    ILogger<GameServerHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.AutoStart)
        {
            // Don't hold up web app startup while the game boots.
            _ = Task.Run(async () =>
            {
                try
                {
                    await server.StartAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not start the Vintage Story server");
                }
            }, CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (server.Snapshot.IsRunning)
        {
            logger.LogInformation("Stopping the Vintage Story server before shutting down");
            await server.StopAsync(CancellationToken.None);
        }
    }
}
