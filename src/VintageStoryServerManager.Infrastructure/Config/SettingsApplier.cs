using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using VintageStoryServerManager.Core.Server;
using VintageStoryServerManager.Infrastructure.WorldSettings;

namespace VintageStoryServerManager.Infrastructure.Config;

/// <summary>
/// Saves world and server settings together and restarts the server so they take effect.
/// World settings go through the running server's console; server settings are written to
/// serverconfig.json while the server is stopped (it rewrites that file when it shuts down).
/// </summary>
public sealed class SettingsApplier(
    WorldSettingsService worldSettings,
    ServerConfigService serverConfig,
    IGameServer server,
    ILogger<SettingsApplier> logger)
{
    /// <returns>A short description of what happened, for the UI.</returns>
    public async Task<string> ApplyAsync(
        IReadOnlyDictionary<string, string> worldChanges,
        IReadOnlyDictionary<string, JsonNode?> serverChanges,
        bool restart,
        CancellationToken cancellationToken = default)
    {
        if (worldChanges.Count == 0 && serverChanges.Count == 0)
        {
            return "Nothing to save.";
        }

        var wasRunning = server.Snapshot.Status == ServerStatus.Running;
        if (worldChanges.Count > 0 && !wasRunning)
        {
            throw new InvalidOperationException("Start the server to change world settings.");
        }

        if (worldChanges.Count > 0)
        {
            await worldSettings.ApplyAsync(worldChanges, cancellationToken);
        }

        if (serverChanges.Count > 0)
        {
            if (wasRunning)
            {
                await server.StopAsync(cancellationToken);
            }

            await serverConfig.UpdateSettingsAsync(serverChanges, cancellationToken);

            if (wasRunning)
            {
                await server.StartAsync(cancellationToken);
                logger.LogInformation("Settings saved; server restarted");
                return "Saved. The server is restarting with the new settings.";
            }

            return worldChanges.Count > 0 ? "Saved." : "Saved. The new settings apply when the server starts.";
        }

        if (restart)
        {
            await server.RestartAsync(cancellationToken);
            logger.LogInformation("World settings saved; server restarted");
            return "Saved. The server is restarting with the new settings.";
        }

        return "Saved. World settings take effect when the server next restarts.";
    }
}
