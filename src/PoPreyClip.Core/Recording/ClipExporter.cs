using PoPreyClip.Clips;
using PoPreyClip.Config;

namespace PoPreyClip.Recording;

/// <summary>Turns the newest replay-buffer segments into an MP4 inside the game's folder.</summary>
internal sealed class ClipExporter(Func<AppConfig> currentConfig, ReplayRecorder recorder, IReadOnlyList<string> trackTitles)
{
    private readonly SemaphoreSlim _busy = new(1, 1);

    public async Task<string?> SaveAsync(string gameName)
    {
        if (!await _busy.WaitAsync(0))
        {
            Log.Warn("Um clipe já está sendo salvo. Aguarde.");
            return null;
        }

        var config = currentConfig();
        try
        {
            var segments = recorder.GetRecentSegments(config.ClipSeconds);
            if (segments.Count == 0)
            {
                Log.Warn("O buffer ainda está vazio. Aguarde alguns segundos e tente de novo.");
                return null;
            }

            var target = ClipPathBuilder.BuildClipPath(config.ClipFolder, gameName, DateTime.Now, config.FilePrefix);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var workDirectory = Path.Combine(recorder.BufferDirectory, "export");
            Directory.CreateDirectory(workDirectory);
            var listFile = Path.Combine(workDirectory, "list.txt");
            var joined = Path.Combine(workDirectory, "joined.ts");

            // 1. Join the segments without re-encoding.
            await File.WriteAllLinesAsync(listFile, segments.Select(s => $"file '{s.Replace("'", "'\\''")}'"));
            await Ffmpeg.RunCheckedAsync(config.FfmpegExe,
                ["-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", listFile, "-map", "0", "-c", "copy", joined],
                "Juntar segmentos");

            // 2. Keep only the last N seconds, name every audio track, and write a seekable MP4.
            var duration = await Ffmpeg.ProbeDurationAsync(config.FfprobeExe, joined);
            var start = Math.Max(0, duration - config.ClipSeconds);
            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-ss", Ffmpeg.Format(start), "-i", joined,
                "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero",
                "-metadata", $"title={gameName}", "-metadata", "comment=PoPreyClip",
            };
            for (var i = 0; i < trackTitles.Count; i++)
            {
                args.AddRange([$"-metadata:s:a:{i}", $"title={trackTitles[i]}", $"-metadata:s:a:{i}", $"handler_name={trackTitles[i]}"]);
                args.AddRange([$"-disposition:a:{i}", i == 0 ? "default" : "0"]);
            }

            args.AddRange(["-movflags", "+faststart", target]);
            await Ffmpeg.RunCheckedAsync(config.FfmpegExe, args, "Exportar clipe");

            File.Delete(joined);
            File.Delete(listFile);
            return target;
        }
        finally
        {
            _busy.Release();
        }
    }
}
