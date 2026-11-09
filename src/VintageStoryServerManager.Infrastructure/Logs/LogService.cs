using System.Text;
using Microsoft.Extensions.Options;
using VintageStoryServerManager.Core.Configuration;

namespace VintageStoryServerManager.Infrastructure.Logs;

public sealed record LogFile(string Name, long SizeBytes, DateTimeOffset Modified)
{
    public bool IsCrashLog => Name.Contains("crash", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The game's log files under <c>Logs/</c>, including <c>Logs/Archive/</c>.</summary>
public sealed class LogService(IOptions<DataPathOptions> paths)
{
    private string Root => paths.Value.LogsDirectory;

    public IReadOnlyList<LogFile> List()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        return Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Select(f => new LogFile(Path.GetRelativePath(Root, f.FullName).Replace('\\', '/'), f.Length, f.LastWriteTimeUtc))
            .OrderByDescending(f => f.IsCrashLog)
            .ThenByDescending(f => f.Modified)
            .ToList();
    }

    /// <summary>The last <paramref name="maxLines"/> lines of a log, read without locking it.</summary>
    public async Task<IReadOnlyList<string>> TailAsync(string name, int maxLines = 500, CancellationToken cancellationToken = default)
    {
        var path = GetPath(name) ?? throw new FileNotFoundException($"Log '{name}' not found.");
        const int chunk = 64 * 1024;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - Math.Max(chunk, maxLines * 400L));
        stream.Seek(start, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new LinkedList<string>();
        if (start > 0)
        {
            await reader.ReadLineAsync(cancellationToken); // skip partial first line
        }

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.AddLast(line);
            if (lines.Count > maxLines)
            {
                lines.RemoveFirst();
            }
        }

        return lines.ToList();
    }

    /// <summary>Full path of a log by its name relative to Logs/, or null if missing or outside Logs/.</summary>
    public string? GetPath(string name)
    {
        var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(Root, name));
        return path.StartsWith(root, StringComparison.Ordinal) && File.Exists(path) ? path : null;
    }
}
