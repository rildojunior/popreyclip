using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PoPreyClip.Clips;
using PoPreyClip.Config;
using PoPreyClip.Recording;

namespace PoPreyClip.Library;

public sealed record ClipInfo(string Path, string Game, DateTime Created, long SizeBytes, bool IsScreenshot = false);

/// <summary>Lists saved clips (one folder per game) and creates cached thumbnails.</summary>
public static class ClipLibrary
{
    private static readonly string ThumbnailDirectory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoPreyClip", "thumbs");

    private static readonly SemaphoreSlim ThumbnailSlots = new(2, 2);

    private static readonly SemaphoreSlim ProbeSlots = new(3, 3);

    /// <summary>&lt;root&gt;\PoPrey Game Capture, with one folder per game inside.</summary>
    public static string CaptureRoot(AppConfig config) => System.IO.Path.Combine(config.ClipFolder, ClipPathBuilder.RootSubfolder);

    /// <summary>
    /// Every .mp4 in &lt;Game&gt;\ and &lt;Game&gt;\Edits\ and every .png/.jpg in &lt;Game&gt;\Screenshots\, newest first.
    /// </summary>
    public static List<ClipInfo> Scan(AppConfig config)
    {
        var result = new List<ClipInfo>();
        var root = CaptureRoot(config);
        if (!Directory.Exists(root))
        {
            return result;
        }

        foreach (var gameDirectory in Directory.EnumerateDirectories(root))
        {
            var game = System.IO.Path.GetFileName(gameDirectory);
            AddFiles(result, gameDirectory, game, ["*.mp4"], isScreenshot: false);
            AddFiles(result, System.IO.Path.Combine(gameDirectory, ClipPathBuilder.EditsSubfolder), game, ["*.mp4"], isScreenshot: false);
            AddFiles(result, System.IO.Path.Combine(gameDirectory, ClipPathBuilder.ScreenshotsSubfolder), game, ["*.png", "*.jpg"], isScreenshot: true);
        }

        return result.OrderByDescending(c => c.Created).ToList();
    }

    /// <summary>
    /// Moves files from the layout used up to 2.1 (&lt;root&gt;\Clips\&lt;Game&gt;\ and &lt;root&gt;\Screenshots\&lt;Game&gt;\)
    /// into the current one. Runs only while the PoPrey Game Capture folder does not exist yet, so in practice once,
    /// on the first start after the update. Only .mp4 clips and .png/.jpg screenshots are moved; anything else stays.
    /// "-editado" clips go to Edits with the "_edit" name. Old folders are removed once empty.
    /// Returns how many files were moved.
    /// </summary>
    public static int MoveOldFolders(AppConfig config)
    {
        var moved = 0;
        var root = CaptureRoot(config);
        if (Directory.Exists(root))
        {
            return 0;
        }

        var oldClips = System.IO.Path.Combine(config.ClipFolder, "Clips");
        var oldScreenshots = System.IO.Path.Combine(config.ClipFolder, ClipPathBuilder.ScreenshotsSubfolder);
        moved += MoveOldFolder(oldClips, ["*.mp4"], (game, file) =>
        {
            var name = System.IO.Path.GetFileName(file);
            var edited = System.Text.RegularExpressions.Regex.Match(name, @"^(.*)-editado(?:-(\d+))?(\.[^.]+)$");
            return edited.Success
                ? System.IO.Path.Combine(root, game, ClipPathBuilder.EditsSubfolder,
                    $"{edited.Groups[1].Value}_edit{(edited.Groups[2].Success ? "_" + edited.Groups[2].Value : "")}{edited.Groups[3].Value}")
                : System.IO.Path.Combine(root, game, name);
        });
        moved += MoveOldFolder(oldScreenshots, ["*.png", "*.jpg"], (game, file) =>
            System.IO.Path.Combine(root, game, ClipPathBuilder.ScreenshotsSubfolder, System.IO.Path.GetFileName(file)));
        if (moved > 0)
        {
            Log.Info($"{moved} arquivo(s) das pastas antigas movido(s) para {root}");
        }

        return moved;
    }

    private static int MoveOldFolder(string oldRoot, string[] patterns, Func<string, string, string> target)
    {
        if (!Directory.Exists(oldRoot))
        {
            return 0;
        }

        var moved = 0;
        foreach (var gameDirectory in Directory.EnumerateDirectories(oldRoot))
        {
            var game = System.IO.Path.GetFileName(gameDirectory);
            foreach (var file in patterns.SelectMany(p => Directory.EnumerateFiles(gameDirectory, p)).ToList())
            {
                try
                {
                    var destination = ClipPathBuilder.EnsureUnique(target(game, file));
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
                    File.Move(file, destination);
                    moved++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Não foi possível mover {file}: {ex.Message}");
                }
            }

            TryDeleteEmpty(gameDirectory);
        }

        TryDeleteEmpty(oldRoot);
        return moved;
    }

    private static void TryDeleteEmpty(string directory)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept; the user can remove it.
        }
    }

    private static void AddFiles(List<ClipInfo> result, string directory, string game, string[] patterns, bool isScreenshot)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in patterns.SelectMany(p => Directory.EnumerateFiles(directory, p)))
        {
            try
            {
                var info = new FileInfo(file);
                result.Add(new ClipInfo(file, game, info.LastWriteTime, info.Length, isScreenshot));
            }
            catch (IOException)
            {
                // File removed while scanning.
            }
        }
    }

    /// <summary>
    /// Path of the cached thumbnail of a clip or screenshot (it may not exist yet). The name changes when the file
    /// changes, so an edited clip gets a new thumbnail.
    /// </summary>
    public static string ThumbnailCachePath(ClipInfo clip) => System.IO.Path.Combine(ThumbnailDirectory, CacheKey(clip) + ".jpg");

    /// <summary>
    /// Returns a cached JPEG thumbnail, creating it with ffmpeg when needed. Null on failure.
    /// For a screenshot it returns the small cached copy when it exists, otherwise the screenshot itself
    /// (the app makes the small copy, see <see cref="ThumbnailCachePath"/>).
    /// </summary>
    public static async Task<string?> GetThumbnailAsync(AppConfig config, ClipInfo clip)
    {
        var target = ThumbnailCachePath(clip);
        if (File.Exists(target))
        {
            return target;
        }

        if (clip.IsScreenshot)
        {
            return clip.Path;
        }

        await ThumbnailSlots.WaitAsync();
        try
        {
            Directory.CreateDirectory(ThumbnailDirectory);
            foreach (var seek in new[] { "1", "0" })
            {
                var result = await Ffmpeg.RunAsync(config.FfmpegExe,
                    ["-hide_banner", "-loglevel", "error", "-y", "-ss", seek, "-i", clip.Path, "-frames:v", "1", "-vf", "scale=480:-2", "-q:v", "4", target]);
                if (result.ExitCode == 0 && File.Exists(target))
                {
                    return target;
                }
            }

            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            ThumbnailSlots.Release();
        }
    }

    /// <summary>Clip length in seconds, read once with ffprobe and then kept next to the thumbnails.</summary>
    public static async Task<double?> GetDurationAsync(AppConfig config, ClipInfo clip)
    {
        if (clip.IsScreenshot)
        {
            return null;
        }

        var cache = System.IO.Path.Combine(ThumbnailDirectory, CacheKey(clip) + ".dur");
        try
        {
            if (File.Exists(cache) &&
                double.TryParse(await File.ReadAllTextAsync(cache), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var cached))
            {
                return cached > 0 ? cached : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read it again with ffprobe.
        }

        await ProbeSlots.WaitAsync();
        try
        {
            var seconds = await Ffmpeg.ProbeDurationAsync(config.FfprobeExe, clip.Path);
            if (seconds <= 0)
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(ThumbnailDirectory);
                await File.WriteAllTextAsync(cache, Ffmpeg.Format(seconds));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not cached; read again next time.
            }

            return seconds;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            ProbeSlots.Release();
        }
    }

    private static string CacheKey(ClipInfo clip) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{clip.Path}|{clip.SizeBytes}|{clip.Created.Ticks}")));

    /// <summary>Opens the clip in the default video player.</summary>
    public static void Play(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>Opens Explorer with the clip selected.</summary>
    public static void RevealInExplorer(string path) => Process.Start("explorer.exe", $"/select,\"{path}\"");

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    public static void MoveToRecycleBin(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
            path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
}
