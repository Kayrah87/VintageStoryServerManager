using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VintageStoryServerManager.Core.Configuration;
using VintageStoryServerManager.Core.Game;
using VintageStoryServerManager.Core.Server;
using VintageStoryServerManager.Core.WorldSettings;

namespace VintageStoryServerManager.Infrastructure.WorldSettings;

/// <summary>
/// World settings (game mode, temporal storms, …). The definitions come from the active game
/// version's own files; current values are read from, and changed on, the running server with
/// <c>/worldconfig</c>, because they live in the world save rather than serverconfig.json.
/// </summary>
public sealed class WorldSettingsService(
    IGameInstallation installation,
    IGameServer server,
    IOptions<DataPathOptions> paths,
    TimeProvider time,
    ILogger<WorldSettingsService> logger)
{
    /// <summary>How long to wait for the server's reply to one command.</summary>
    internal TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Unanswered commands in a row after which the server is treated as not responding.</summary>
    private const int MaxMissedReplies = 3;
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Lock _schemaGate = new();
    private (string Directory, WorldSettingsSchema? Schema)? _schemaCache;

    private string CachePath => Path.Combine(paths.Value.ManagerDirectory, "world-settings.json");

    /// <summary>The world setting definitions for the active game version, or null if they can't be read.</summary>
    public WorldSettingsSchema? GetSchema()
    {
        var directory = installation.ActiveDirectory;
        lock (_schemaGate)
        {
            if (_schemaCache is { } cached && cached.Directory == directory)
            {
                return cached.Schema;
            }
        }

        WorldSettingsSchema? schema = null;
        try
        {
            var dll = Path.Combine(directory, "Mods", "VSSurvivalMod.dll");
            var definitions = File.Exists(dll) ? ExtractDefinitions(File.ReadAllBytes(dll)) : null;
            if (definitions is not null)
            {
                schema = WorldSettingsParser.Parse(definitions, ReadLanguage(Path.Combine(directory, "assets", "game", "lang", "en.json")));
            }
            else
            {
                logger.LogWarning("Could not find world setting definitions in {Directory}", directory);
            }
        }
        catch (Exception ex)
        {
            // Unexpected game file layouts must not break the settings page; it falls back to the raw editor.
            logger.LogWarning(ex, "Could not read world setting definitions from {Directory}", directory);
        }

        lock (_schemaGate)
        {
            _schemaCache = (directory, schema);
        }

        return schema;
    }

    /// <summary>The values last read from the server, or null if they have never been read.</summary>
    public WorldSettingValues? GetCached()
    {
        try
        {
            return File.Exists(CachePath)
                ? System.Text.Json.JsonSerializer.Deserialize<WorldSettingValues>(File.ReadAllText(CachePath))
                : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Asks the running server for every world setting's current value.</summary>
    public async Task<WorldSettingValues> ReadCurrentAsync(CancellationToken cancellationToken = default)
    {
        var schema = GetSchema() ?? throw new InvalidOperationException("The game's world setting definitions couldn't be read.");
        RequireRunning();

        // One command at a time, each after the previous reply: when the game is busy it can read
        // several queued console lines as one command, which would turn "/worldconfig a" followed by
        // "/worldconfig b" into setting a to "/worldconfig b".
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var notSet = new List<string>();
        var missed = 0;
        foreach (var setting in schema.Settings)
        {
            var reply = await SendAndAwaitReplyAsync(
                $"/worldconfig {setting.Code}",
                text => IsReplyFor(text, setting.Code),
                cancellationToken);
            if (reply is null)
            {
                // Keep going: one slow or lost reply shouldn't leave every later setting unread.
                if (++missed >= MaxMissedReplies)
                {
                    logger.LogWarning("The server stopped answering /worldconfig; read {Count} of {Total} settings", values.Count + notSet.Count, schema.Settings.Count);
                    break;
                }

                continue;
            }

            missed = 0;
            if (WorldSettingsParser.TryParseCurrentValue(reply, out _, out var value))
            {
                values[setting.Code] = value;
            }
            else if (WorldSettingsParser.TryParseNotSet(reply, out _))
            {
                notSet.Add(setting.Code);
            }

            // Otherwise the game won't say (world size): the value stays unknown.
        }

        if (values.Count == 0 && notSet.Count == 0)
        {
            throw new TimeoutException("The server didn't report any world settings. Is it still starting?");
        }

        var result = new WorldSettingValues(values, time.GetUtcNow(), notSet);
        SaveCache(result);
        return result;
    }

    /// <summary>
    /// Validates and sends changed world settings to the running server. They take effect when
    /// the server restarts.
    /// </summary>
    public async Task ApplyAsync(IReadOnlyDictionary<string, string> changes, CancellationToken cancellationToken = default)
    {
        if (changes.Count == 0)
        {
            return;
        }

        var schema = GetSchema() ?? throw new InvalidOperationException("The game's world setting definitions couldn't be read.");
        var commands = new List<(string Code, string Value)>();
        foreach (var (code, value) in changes)
        {
            var definition = schema.Find(code) ?? throw new ArgumentException($"Unknown world setting '{code}'.");
            if (definition.OnlyDuringWorldCreate)
            {
                throw new ArgumentException($"'{definition.Label}' can only be set when a world is created.");
            }

            var normalized = definition.Normalize(value) ?? throw new ArgumentException($"'{value}' is not a valid value for '{definition.Label}'.");
            commands.Add((definition.Code, normalized));
        }

        RequireRunning();

        // One command at a time, each after the previous confirmation (see ReadCurrentAsync).
        var confirmed = 0;
        var created = new List<string>();
        foreach (var (code, value) in commands)
        {
            var reply = await SendAndAwaitReplyAsync(
                $"/worldconfig {code} {value}",
                text => WorldSettingsParser.IsValueSetConfirmation(text) || WorldSettingsParser.TryParseNotSet(text, out var missing) && Same(missing, code),
                cancellationToken);
            if (reply is not null && WorldSettingsParser.TryParseNotSet(reply, out _))
            {
                // The world was created without this setting; /worldconfig only changes existing ones.
                var definition = schema.Find(code)!;
                reply = await SendAndAwaitReplyAsync(
                    $"/worldconfigcreate {definition.CreateType} {code} {value}",
                    WorldSettingsParser.IsValueSetConfirmation,
                    cancellationToken);
                if (reply is not null)
                {
                    created.Add(code);
                }
            }

            if (reply is null)
            {
                break;
            }

            confirmed++;
        }

        if (confirmed < commands.Count)
        {
            throw new InvalidOperationException($"The server confirmed {confirmed} of {commands.Count} world setting changes. Check the console.");
        }

        logger.LogInformation("Changed world settings: {Changes}{Created}", string.Join(", ", commands.Select(c => $"{c.Code}={c.Value}")),
            created.Count == 0 ? "" : $" (added to the world: {string.Join(", ", created)})");

        var cached = GetCached();
        var updated = new Dictionary<string, string>(cached?.Values ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (code, value) in commands)
        {
            updated[code] = value;
        }

        var stillNotSet = (cached?.NotSet ?? []).Where(c => !commands.Any(command => Same(command.Code, c))).ToList();
        SaveCache(new WorldSettingValues(updated, time.GetUtcNow(), stillNotSet));
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>"x currently has value: …", "No such config found: x", or the world size refusal.</summary>
    private static bool IsReplyFor(string text, string code) =>
        WorldSettingsParser.TryParseCurrentValue(text, out var current, out _) && Same(current, code) ||
        WorldSettingsParser.TryParseNotSet(text, out var missing) && Same(missing, code) ||
        WorldSettingsParser.IsWorldSizeRefusal(text);

    /// <summary>
    /// Sends a console command and waits for the first output line that <paramref name="isReply"/>
    /// accepts. Returns that line, or null if none arrives within <see cref="ResponseTimeout"/>.
    /// </summary>
    private async Task<string?> SendAndAwaitReplyAsync(string command, Func<string, bool> isReply, CancellationToken cancellationToken)
    {
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLine(ConsoleLine line)
        {
            if (isReply(line.Text))
            {
                reply.TrySetResult(line.Text);
            }
        }

        server.ConsoleOutput += OnLine;
        try
        {
            await server.SendCommandAsync(command, cancellationToken);
            var finished = await Task.WhenAny(reply.Task, Task.Delay(ResponseTimeout, time, cancellationToken));
            if (finished == reply.Task)
            {
                return await reply.Task;
            }

            cancellationToken.ThrowIfCancellationRequested();
            logger.LogWarning("No reply from the server to {Command}", command);
            return null;
        }
        finally
        {
            server.ConsoleOutput -= OnLine;
        }
    }

    /// <summary>
    /// Finds the world config definitions (a JSON object with <c>playstyles</c> and
    /// <c>worldConfigAttributes</c>) embedded as text in VSSurvivalMod.dll.
    /// </summary>
    public static JObject? ExtractDefinitions(byte[] assembly)
    {
        var anchor = assembly.AsSpan().IndexOf("\"playstyles\""u8);
        if (anchor < 0)
        {
            return null;
        }

        var settings = new JsonLoadSettings { CommentHandling = CommentHandling.Ignore };
        var position = anchor;
        for (var attempt = 0; attempt < 400 && position > 0; attempt++)
        {
            position = assembly.AsSpan(0, position).LastIndexOf((byte)'{');
            if (position < 0)
            {
                break;
            }

            var length = Math.Min(assembly.Length - position, 2 * 1024 * 1024);
            var text = Encoding.UTF8.GetString(assembly, position, length);
            try
            {
                using var reader = new JsonTextReader(new StringReader(text));
                if (JObject.Load(reader, settings) is { } candidate &&
                    candidate["playstyles"] is JArray &&
                    candidate["worldConfigAttributes"] is JArray)
                {
                    return candidate;
                }
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // Not the start of the object; keep looking further back.
            }
        }

        return null;
    }

    private static Dictionary<string, string>? ReadLanguage(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var root = JObject.Parse(File.ReadAllText(path), new JsonLoadSettings
        {
            CommentHandling = CommentHandling.Ignore,
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Replace,
        });

        // Keys can differ only by case (e.g. "unknown" and "Unknown"); the first one wins.
        var language = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.Properties().Where(p => p.Value.Type == JTokenType.String))
        {
            language.TryAdd(property.Name, property.Value.ToString());
        }

        return language;
    }

    private void SaveCache(WorldSettingValues values)
    {
        Directory.CreateDirectory(paths.Value.ManagerDirectory);
        File.WriteAllText(CachePath, System.Text.Json.JsonSerializer.Serialize(values, JsonOptions));
    }

    private void RequireRunning()
    {
        if (server.Snapshot.Status != ServerStatus.Running)
        {
            throw new InvalidOperationException("Start the server first: world settings are read and changed through its console.");
        }
    }
}
