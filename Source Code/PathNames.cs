using System.Text;
using System.Text.RegularExpressions;

namespace LocalInventoryExport;

internal static partial class PathNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    internal static string SignatureOf(string? uri)
    {
        if (string.IsNullOrEmpty(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) || parsed.Segments.Length < 2)
            return "";

        return Path.GetFileNameWithoutExtension(parsed.Segments[1]).ToLowerInvariant();
    }

    internal static string ManifestPrint(IEnumerable<string>? hashes)
    {
        string[] sorted = (hashes ?? []).Where(hash => !string.IsNullOrEmpty(hash)).Select(hash => hash.ToLowerInvariant()).Distinct().Order().ToArray();
        if (sorted.Length == 0)
            return "";

        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(string.Join(",", sorted))));
    }

    internal static string Norm(string? path) => (path ?? "").Replace('\\', '/');

    internal static string DirectoryOf(string? path)
    {
        string normal = Norm(path);
        int slash = normal.LastIndexOf('/');
        return slash < 0 ? "" : normal[..slash];
    }

    private static readonly HashSet<char> Invalid = new(Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*"));

    internal static string Clean(string? name, int maximum = 100)
    {
        string text = Tags().Replace(name ?? "", "");
        StringBuilder builder = new(text.Length);
                foreach (char c in text)
        {
            if (c < 32 || Invalid.Contains(c))
                builder.Append('_');
            else
                builder.Append(c);
        }
        string result = builder.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length > maximum)
            result = result[..maximum].TrimEnd('.', ' ');

        if (result.Length == 0)
            result = "Unnamed";

        if (Reserved.Contains(result))
            result += "_";

        return result;
    }

    internal static string[] Components(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return [];

        return path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Select(part => Clean(part, 60)).ToArray();
    }

    internal static string Unique(string name, HashSet<string> used)
    {
        string candidate = name;
        int index = 2;
        while (!used.Add(candidate.ToLowerInvariant()))
            candidate = $"{name} ({index++})";

        return candidate;
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:F1} {units[unit]}";
    }

    internal static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            return "unknown";

        TimeSpan span = TimeSpan.FromSeconds(seconds);
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours} hours {span.Minutes} minutes";

        if (span.TotalMinutes >= 1)
            return $"{span.Minutes} minutes {span.Seconds} seconds";

        return $"{span.Seconds} seconds";
    }
}
