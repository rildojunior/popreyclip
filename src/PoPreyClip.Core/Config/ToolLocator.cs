namespace PoPreyClip.Config;

/// <summary>
/// Finds ffmpeg/ffprobe even when the current PATH is stale (for example a terminal or Explorer
/// window opened before FFmpeg was installed with winget).
/// </summary>
internal static class ToolLocator
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Resolve(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured) || Path.IsPathRooted(configured))
        {
            return configured;
        }

        if (Cache.TryGetValue(configured, out var cached) && File.Exists(cached))
        {
            return cached;
        }

        var resolved = Search(configured);
        if (!ReferenceEquals(resolved, configured))
        {
            Cache[configured] = resolved;
        }

        return resolved;
    }

    private static string Search(string configured)
    {

        var fileName = configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? configured : configured + ".exe";
        foreach (var directory in CandidateDirectories())
        {
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }

        return configured;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        yield return AppContext.BaseDirectory;

        var paths = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
        };
        foreach (var entry in paths.Where(p => p is not null).SelectMany(p => p!.Split(';', StringSplitOptions.RemoveEmptyEntries)))
        {
            yield return Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
        }

        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet");
        yield return Path.Combine(winget, "Links");

        var packages = Path.Combine(winget, "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var package in Directory.EnumerateDirectories(packages, "*FFmpeg*"))
            {
                foreach (var bin in Directory.EnumerateDirectories(package, "bin", SearchOption.AllDirectories))
                {
                    yield return bin;
                }
            }
        }
    }
}
