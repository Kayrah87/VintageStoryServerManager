namespace VintageStoryServerManager.Core.Versioning;

/// <summary>
/// Lenient semantic-version comparison for game and mod versions such as
/// <c>1.22.7</c>, <c>v0.8.4</c>, <c>1.0.0-rc.2</c> or <c>2.1</c>.
/// </summary>
public static class ModVersion
{
    public static int Compare(string? left, string? right)
    {
        var (leftNumbers, leftPre) = Split(left);
        var (rightNumbers, rightPre) = Split(right);

        var length = Math.Max(leftNumbers.Length, rightNumbers.Length);
        for (var i = 0; i < length; i++)
        {
            var l = i < leftNumbers.Length ? leftNumbers[i] : 0;
            var r = i < rightNumbers.Length ? rightNumbers[i] : 0;
            if (l != r)
            {
                return l.CompareTo(r);
            }
        }

        // A release sorts after its pre-releases: 1.0.0 > 1.0.0-rc.1.
        return (leftPre, rightPre) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => ComparePreRelease(leftPre, rightPre),
        };
    }

    /// <summary>
    /// True when <paramref name="installed"/> meets a Vintage Story dependency requirement.
    /// A requirement is a minimum version; empty or <c>*</c> means any version.
    /// </summary>
    public static bool Satisfies(string? installed, string? required)
    {
        if (string.IsNullOrWhiteSpace(required) || required.Trim() == "*")
        {
            return true;
        }

        return installed is not null && Compare(installed, required) >= 0;
    }

    /// <summary>The <c>major.minor</c> part, e.g. <c>1.22</c> for <c>1.22.7</c>.</summary>
    public static string MajorMinor(string version)
    {
        var (numbers, _) = Split(version);
        return numbers.Length switch
        {
            0 => version,
            1 => $"{numbers[0]}.0",
            _ => $"{numbers[0]}.{numbers[1]}",
        };
    }

    private static (int[] Numbers, string? PreRelease) Split(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return ([], null);
        }

        var text = version.Trim().TrimStart('v', 'V');
        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        string? pre = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            pre = text[(dash + 1)..];
            text = text[..dash];
        }

        var numbers = text.Split('.')
            .Select(part => int.TryParse(new string(part.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0)
            .ToArray();
        return (numbers, string.IsNullOrEmpty(pre) ? null : pre);
    }

    private static int ComparePreRelease(string left, string right)
    {
        var l = left.Split('.');
        var r = right.Split('.');
        for (var i = 0; i < Math.Min(l.Length, r.Length); i++)
        {
            var bothNumeric = int.TryParse(l[i], out var ln) & int.TryParse(r[i], out var rn);
            var result = bothNumeric ? ln.CompareTo(rn) : string.Compare(l[i], r[i], StringComparison.OrdinalIgnoreCase);
            if (result != 0)
            {
                return result;
            }
        }

        return l.Length.CompareTo(r.Length);
    }
}
