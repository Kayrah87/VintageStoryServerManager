using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VintageStoryServerManager.Core.Backups;
using VintageStoryServerManager.Core.Configuration;
using VintageStoryServerManager.Core.Server;

namespace VintageStoryServerManager.Infrastructure.Config;

/// <summary>Reads and edits <c>serverconfig.json</c>.</summary>
public sealed class ServerConfigService(
    IOptions<DataPathOptions> paths,
    IGameServer server,
    IBackupService backups,
    MaintenanceLock maintenance,
    ILogger<ServerConfigService> logger)
{
    private string ConfigPath => paths.Value.ServerConfigPath;

    public bool Exists => File.Exists(ConfigPath);

    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default) =>
        Exists ? await File.ReadAllTextAsync(ConfigPath, cancellationToken) : null;

    /// <summary>Parses the config; null when it doesn't exist yet or can't be read.</summary>
    public JsonObject? TryReadObject()
    {
        try
        {
            return Exists ? JsonNode.Parse(File.ReadAllText(ConfigPath)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Returns an error message when <paramref name="json"/> isn't a valid config object.</summary>
    public static string? Validate(string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions()) is JsonObject
                ? null
                : "The config must be a JSON object ({ … }).";
        }
        catch (JsonException ex)
        {
            return $"Invalid JSON: {ex.Message}";
        }
    }

    /// <summary>
    /// Changes individual top-level settings (e.g. ServerName, MaxClients) and saves the config,
    /// keeping everything else as it is. Same rules as <see cref="SaveAsync"/>.
    /// </summary>
    public async Task UpdateSettingsAsync(IReadOnlyDictionary<string, JsonNode?> changes, CancellationToken cancellationToken = default)
    {
        if (changes.Count == 0)
        {
            return;
        }

        var config = TryReadObject() ?? (Exists
            ? throw new InvalidDataException("serverconfig.json can't be read. Fix it in the raw editor first.")
            : new JsonObject());

        foreach (var (key, value) in changes)
        {
            config[key] = value?.DeepClone();
        }

        await SaveAsync(config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    /// <summary>
    /// Saves the config after backing up the current one. The server must be stopped: it
    /// rewrites serverconfig.json from memory when it shuts down, which would undo the edit.
    /// </summary>
    public async Task SaveAsync(string json, CancellationToken cancellationToken = default)
    {
        if (server.Snapshot.IsRunning || server.Snapshot.Status == ServerStatus.Stopping)
        {
            throw new InvalidOperationException("Stop the server before editing serverconfig.json; it overwrites the file when it shuts down.");
        }

        if (Validate(json) is { } error)
        {
            throw new InvalidDataException(error);
        }

        using var _ = await maintenance.AcquireAsync(cancellationToken);
        if (Exists)
        {
            await backups.CreateAsync(BackupScope.Config, "before editing serverconfig.json", automatic: true, cancellationToken);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var temp = ConfigPath + ".tmp";
        await File.WriteAllTextAsync(temp, json, cancellationToken);
        File.Move(temp, ConfigPath, overwrite: true);
        logger.LogInformation("serverconfig.json updated");
    }
}
