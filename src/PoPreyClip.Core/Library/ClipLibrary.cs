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

    public static string ClipsRoot(AppConfig config) => System.IO.Path.Combine(config.ClipFolder, ClipPathBuilder.ClipsSubfolder);

    public static string ScreenshotsRoot(AppConfig config) => System.IO.Path.Combine(config.ClipFolder, ClipPathBuilder.ScreenshotsSubfolder);

    /// <summary>Every .mp4 in &lt;root&gt;\Clips\&lt;Game&gt;\ and every .png/.jpg in &lt;root&gt;\Screenshots\&lt;Game&gt;\, newest first.</summary>
    public static List<ClipInfo> Scan(AppConfig config)
    {
        var result = new List<ClipInfo>();
        AddFiles(result, ClipsRoot(config), ["*.mp4"], isScreenshot: false);
        AddFiles(result, ScreenshotsRoot(config), ["*.png", "*.jpg"], isScreenshot: true);
        return result.OrderByDescending(c => c.Created).ToList();
    }

    private static void AddFiles(List<ClipInfo> result, string root, string[] patterns, bool isScreenshot)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var gameDirectory in Directory.EnumerateDirectories(root))
        {
            var game = System.IO.Path.GetFileName(gameDirectory);
            foreach (var file in patterns.SelectMany(p => Directory.EnumerateFiles(gameDirectory, p)))
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
    }

    /// <summary>Returns a cached JPEG thumbnail, creating it with ffmpeg when needed. Null on failure.</summary>
    public static async Task<string?> GetThumbnailAsync(AppConfig config, ClipInfo clip)
    {
        if (clip.IsScreenshot)
        {
            return clip.Path;
        }

        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{clip.Path}|{clip.SizeBytes}|{clip.Created.Ticks}")));
        var target = System.IO.Path.Combine(ThumbnailDirectory, key + ".jpg");
        if (File.Exists(target))
        {
            return target;
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

    public static async Task<double?> GetDurationAsync(AppConfig config, ClipInfo clip)
    {
        if (clip.IsScreenshot)
        {
            return null;
        }

        try
        {
            var seconds = await Ffmpeg.ProbeDurationAsync(config.FfprobeExe, clip.Path);
            return seconds > 0 ? seconds : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

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
