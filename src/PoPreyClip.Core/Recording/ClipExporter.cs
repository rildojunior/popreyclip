using System.Globalization;
using PoPreyClip.Clips;
using PoPreyClip.Config;

namespace PoPreyClip.Recording;

/// <summary>
/// Turns the newest replay-buffer segments into an MP4 inside the game's folder. Video and audio come from
/// separate segment files that share one time base, so they are joined by their original timestamps.
/// </summary>
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
            var finished = await recorder.WaitForCurrentSegmentAsync();
            var videoSegments = recorder.GetRecentSegments(ReplayRecorder.VideoSegmentPrefix, config.ClipSeconds, finished);
            var audioSegments = recorder.HasAudio ? recorder.GetRecentSegments(ReplayRecorder.AudioSegmentPrefix, config.ClipSeconds + config.Video.SegmentSeconds) : [];
            if (videoSegments.Count == 0 || (recorder.HasAudio && audioSegments.Count == 0))
            {
                Log.Warn("O buffer ainda está vazio. Aguarde alguns segundos e tente de novo.");
                return null;
            }

            var target = ClipPathBuilder.BuildClipPath(config.ClipFolder, gameName, DateTime.Now, config.FilePrefix);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var workDirectory = Path.Combine(recorder.BufferDirectory, "export");
            Directory.CreateDirectory(workDirectory);
            var video = Path.Combine(workDirectory, "video.ts");
            var audio = Path.Combine(workDirectory, "audio.ts");

            // 1. MPEG-TS segments can simply be appended; their timestamps continue from one to the next.
            await JoinAsync(videoSegments, video);
            if (recorder.HasAudio)
            {
                await JoinAsync(audioSegments, audio);
            }

            // 2. The clip ends where the recording ends now and starts at the keyframe just before "end - N seconds"
            //    (without re-encoding, video can only start at a keyframe).
            var packets = await Ffmpeg.RunCheckedAsync(config.FfprobeExe,
                ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,dts_time,flags", "-of", "csv=p=0", video], "ffprobe");
            var end = recorder.HasAudio ? await ProbeEndAsync(config.FfprobeExe, audio) : LastTimestamp(packets.StandardOutput);
            var start = KeyframeStart(packets.StandardOutput, end - config.ClipSeconds);

            // 3. Join video and audio by their timestamps (-copyts), cut, name every audio track, write a seekable MP4.
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-copyts", "-i", video };
            if (recorder.HasAudio)
            {
                args.AddRange(["-i", audio]);
            }

            args.AddRange(["-map", "0:v:0"]);
            if (recorder.HasAudio)
            {
                args.AddRange(["-map", "1:a"]);
            }

            args.AddRange([
                "-c", "copy", "-ss", Ffmpeg.Format(start), "-avoid_negative_ts", "make_zero",
                "-metadata", $"title={gameName}", "-metadata", "comment=PoPreyClip",
            ]);
            for (var i = 0; i < trackTitles.Count; i++)
            {
                args.AddRange([$"-metadata:s:a:{i}", $"title={trackTitles[i]}", $"-metadata:s:a:{i}", $"handler_name={trackTitles[i]}"]);
                args.AddRange([$"-disposition:a:{i}", i == 0 ? "default" : "0"]);
            }

            args.AddRange(["-movflags", "+faststart", target]);
            await Ffmpeg.RunCheckedAsync(config.FfmpegExe, args, "Exportar clipe");

            File.Delete(video);
            File.Delete(audio);
            return target;
        }
        finally
        {
            _busy.Release();
        }
    }

    private static async Task JoinAsync(IEnumerable<string> segments, string target)
    {
        await using var output = File.Create(target);
        foreach (var segment in segments)
        {
            // ffmpeg may still be writing the newest segment; share the file instead of locking it.
            await using var input = new FileStream(segment, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await input.CopyToAsync(output);
        }
    }

    private static async Task<double> ProbeEndAsync(string ffprobe, string file)
    {
        var result = await Ffmpeg.RunCheckedAsync(ffprobe,
            ["-v", "error", "-show_entries", "format=start_time,duration", "-of", "csv=p=0", file], "ffprobe");
        var parts = result.StandardOutput.Trim().Split(',');
        return parts.Length == 2 && ParseSeconds(parts[0]) is { } start && ParseSeconds(parts[1]) is { } duration ? start + duration : 0;
    }

    private static double LastTimestamp(string packetLines) =>
        packetLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => ParseSeconds(line.Split(',')[0]) ?? 0)
            .DefaultIfEmpty(0)
            .Max();

    /// <summary>
    /// From ffprobe "pts_time,dts_time,flags" lines: where to cut so the clip starts on the latest keyframe shown at
    /// or before <paramref name="wanted"/> (the first keyframe if none is that early). The cut uses the keyframe's
    /// decode time, a hair earlier, because ffmpeg compares decode times when it drops packets before the cut.
    /// </summary>
    internal static double KeyframeStart(string packetLines, double wanted)
    {
        (double Pts, double Dts)? best = null;
        (double Pts, double Dts)? first = null;
        foreach (var line in packetLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 3 || !parts[2].Contains('K') || ParseSeconds(parts[0]) is not { } pts || ParseSeconds(parts[1]) is not { } dts)
            {
                continue;
            }

            if (first is null || pts < first.Value.Pts)
            {
                first = (pts, dts);
            }

            if (pts <= wanted + 0.001 && (best is null || pts > best.Value.Pts))
            {
                best = (pts, dts);
            }
        }

        return (best ?? first) is { } keyframe ? Math.Max(0, keyframe.Dts - 0.001) : Math.Max(0, wanted);
    }

    private static double? ParseSeconds(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}
