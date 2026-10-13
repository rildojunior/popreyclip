using System.Text;

namespace PoPreyClip.Clips;

/// <summary>
/// Builds export paths with one folder per game:
/// &lt;root&gt;\Clips\&lt;Game Name&gt;\&lt;Prefix&gt;&lt;GameName&gt;&lt;yyyyMMddHHmmssfff&gt;.mp4
/// The folder keeps the readable game name; the file name drops spaces and symbols.
/// </summary>
public static class ClipPathBuilder
{
    public const string ClipsSubfolder = "Clips";
    public const string ScreenshotsSubfolder = "Screenshots";
    public const string UnknownGame = "Unknown Game";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string BuildClipPath(string root, string gameName, DateTime timestamp, string prefix = "PoPreyClip")
    {
        var folder = GetGameFolder(root, gameName);
        var fileName = $"{CompactName(prefix)}{CompactName(gameName)}{timestamp:yyyyMMddHHmmssfff}.mp4";
        return EnsureUnique(Path.Combine(folder, fileName));
    }

    /// <summary>&lt;root&gt;Screenshots&lt;Game Name&gt;&lt;Prefix&gt;&lt;GameName&gt;&lt;yyyyMMddHHmmssfff&gt;.png</summary>
    public static string BuildScreenshotPath(string root, string gameName, DateTime timestamp, string prefix = "PoPreyClip")
    {
        var folder = Path.Combine(root, ScreenshotsSubfolder, SanitizeFolderName(gameName));
        var fileName = $"{CompactName(prefix)}{CompactName(gameName)}{timestamp:yyyyMMddHHmmssfff}.png";
        return EnsureUnique(Path.Combine(folder, fileName));
    }

    public static string GetGameFolder(string root, string gameName) =>
        Path.Combine(root, ClipsSubfolder, SanitizeFolderName(gameName));

    /// <summary>Makes a game name safe as a Windows folder name while keeping it readable.</summary>
    public static string SanitizeFolderName(string? gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName))
        {
            return UnknownGame;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(gameName.Length);
        foreach (var c in gameName)
        {
            if (Array.IndexOf(invalid, c) >= 0 || char.IsControl(c))
            {
                builder.Append(' ');
            }
            else if (c is '™' or '®' or '©')
            {
                continue;
            }
            else
            {
                builder.Append(c);
            }
        }

        var cleaned = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');
        if (cleaned.Length == 0)
        {
            return UnknownGame;
        }

        if (cleaned.Length > 80)
        {
            cleaned = cleaned[..80].TrimEnd('.', ' ');
        }

        return ReservedNames.Contains(cleaned) ? cleaned + "_" : cleaned;
    }

    /// <summary>Keeps only letters and digits, e.g. "ARC Raiders" becomes "ARCRaiders".</summary>
    public static string CompactName(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string EnsureUnique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name}_{i}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
