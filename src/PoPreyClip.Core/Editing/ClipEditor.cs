using System.Diagnostics;
using System.Globalization;
using PoPreyClip.Audio;
using PoPreyClip.Config;
using PoPreyClip.Recording;

namespace PoPreyClip.Editing;

/// <summary>Part of the picture to keep, in source pixels.</summary>
public sealed record CropRect(int X, int Y, int Width, int Height);

/// <summary>
/// What the user chose in the editor. <see cref="Volumes"/> holds one percentage (0 to 200) per
/// <see cref="ClipMedia.SourceTracks"/> entry.
/// </summary>
public sealed record ClipEditPlan(double Start, double End, CropRect? Crop, IReadOnlyList<int> Volumes, bool MergeAudio)
{
    public bool ChangesAudio => MergeAudio || Volumes.Any(v => v != 100);
}

/// <summary>
/// Writes an edited copy of a clip with one ffmpeg run. The saved clip always starts exactly where the user chose: when
/// that is a keyframe and there is no crop the video is copied as is (instant, no quality loss); otherwise it is
/// re-encoded with the recording encoder.
/// Audio is copied unless a volume changed or the tracks are merged; then only the audio is encoded again.
/// </summary>
public static class ClipEditor
{
    /// <summary>Peak ceiling for boosted or mixed tracks (about -1.5 dBFS), same as the recorder's limiter.</summary>
    private const string Limiter = "alimiter=limit=0.84:level=disabled:latency=1";

    /// <summary>
    /// A copy can only begin on a keyframe (every 0.5 to 2 seconds), so any other start, or a crop, needs the video
    /// encoded again. Trimming only the end, or starting on a keyframe, stays a copy.
    /// </summary>
    public static bool NeedsReencode(ClipMedia media, ClipEditPlan plan) => plan.Crop is not null || !media.IsKeyframe(plan.Start);

    /// <summary>Where ffmpeg starts reading: the chosen start, or the keyframe it falls on when copying.</summary>
    private static double EffectiveStart(ClipMedia media, ClipEditPlan plan) =>
        NeedsReencode(media, plan) ? plan.Start : media.KeyframeAtOrBefore(plan.Start);

    /// <summary>"Clip-editado.mp4", or "Clip-editado-2.mp4" and so on when that name is taken.</summary>
    public static string NewFileName(string original)
    {
        var directory = Path.GetDirectoryName(original) ?? "";
        var name = Path.GetFileNameWithoutExtension(original);
        var extension = Path.GetExtension(original);
        var candidate = Path.Combine(directory, $"{name}-editado{extension}");
        for (var i = 2; File.Exists(candidate); i++)
        {
            candidate = Path.Combine(directory, $"{name}-editado-{i}{extension}");
        }

        return candidate;
    }

    internal static List<string> BuildArguments(AppConfig config, ClipMedia media, ClipEditPlan plan, string encoder, string output)
    {
        var reencode = NeedsReencode(media, plan);
        var start = EffectiveStart(media, plan);
        // A copy has to begin on a keyframe; seeking a hair after it makes sure ffmpeg does not pick the one before.
        var seek = reencode ? start : start + 0.001;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-nostats", "-progress", "pipe:1" };
        if (reencode)
        {
            args.AddRange(["-hwaccel", "d3d11va"]);
        }

        args.AddRange(["-ss", Ffmpeg.Format(seek), "-to", Ffmpeg.Format(plan.End), "-i", media.Path, "-map", "0:v:0"]);
        if (plan.Crop is { } crop)
        {
            args.AddRange(["-vf", $"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}"]);
        }

        if (reencode)
        {
            args.AddRange(VideoEncoderArguments(encoder, Math.Max(1, config.Video.Quality - 2)));
        }
        else
        {
            args.AddRange(["-c:v", "copy"]);
        }

        // Copied audio would begin at the keyframe ffmpeg seeked to, up to 2 s before the first re-encoded frame, so an
        // exact start trims the audio exactly too.
        AddAudio(args, config, media, plan, encode: reencode || plan.ChangesAudio);
        args.AddRange(["-map_metadata", "0", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", "-f", "mp4", output]);
        return args;
    }

    private static void AddAudio(List<string> args, AppConfig config, ClipMedia media, ClipEditPlan plan, bool encode)
    {
        var sources = media.SourceTracks;
        if (sources.Count == 0)
        {
            return;
        }

        if (!encode)
        {
            args.AddRange(["-map", "0:a", "-c:a", "copy"]);
            return;
        }

        var filters = new List<string>();
        var outputs = new List<(string Label, string Title)>();
        var needsMix = plan.MergeAudio ? sources.Count > 1 : media.HasMixTrack;
        var keepSeparate = !plan.MergeAudio;
        var mixInputs = new List<string>();
        for (var i = 0; i < sources.Count; i++)
        {
            var volume = Math.Clamp(i < plan.Volumes.Count ? plan.Volumes[i] : 100, 0, 200);
            var chain = $"[0:a:{sources[i].AudioIndex}]volume={(volume / 100.0).ToString("0.###", CultureInfo.InvariantCulture)}";
            if (needsMix && keepSeparate)
            {
                filters.Add($"{chain},asplit=2[m{i}][t{i}]");
                mixInputs.Add($"[m{i}]");
            }
            else if (needsMix)
            {
                filters.Add($"{chain}[m{i}]");
                mixInputs.Add($"[m{i}]");
            }
            else
            {
                filters.Add($"{chain}{(volume > 100 ? "," + Limiter : "")}[t{i}]");
            }
        }

        if (needsMix)
        {
            filters.Add($"{string.Concat(mixInputs)}amix=inputs={mixInputs.Count}:normalize=0:duration=longest,{Limiter}[mix]");
            outputs.Add(("[mix]", AudioTrackPlan.MixTitle));
        }

        if (keepSeparate)
        {
            for (var i = 0; i < sources.Count; i++)
            {
                var volume = i < plan.Volumes.Count ? plan.Volumes[i] : 100;
                if (needsMix && volume > 100)
                {
                    filters.Add($"[t{i}]{Limiter}[l{i}]");
                    outputs.Add(($"[l{i}]", sources[i].Title));
                }
                else
                {
                    outputs.Add(($"[t{i}]", sources[i].Title));
                }
            }
        }
        else if (!needsMix)
        {
            // Merging a clip that has a single track: that track, with its new volume.
            outputs.Add(("[t0]", sources[0].Title));
        }

        args.AddRange(["-filter_complex", string.Join(';', filters)]);
        foreach (var output in outputs)
        {
            args.AddRange(["-map", output.Label]);
        }

        args.AddRange(["-c:a", "aac", "-b:a", $"{config.Audio.AudioBitrateKbps}k"]);
        for (var i = 0; i < outputs.Count; i++)
        {
            args.AddRange([
                $"-metadata:s:a:{i}", $"title={outputs[i].Title}",
                $"-metadata:s:a:{i}", $"handler_name={outputs[i].Title}",
                $"-disposition:a:{i}", i == 0 ? "default" : "0",
            ]);
        }
    }

    internal static List<string> VideoEncoderArguments(string encoder, int quality)
    {
        var q = quality.ToString(CultureInfo.InvariantCulture);
        List<string> args = encoder switch
        {
            _ when encoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase) =>
                ["-c:v", encoder, "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", q, "-b:v", "0"],
            _ when encoder.EndsWith("_amf", StringComparison.OrdinalIgnoreCase) =>
                ["-c:v", encoder, "-usage", "transcoding", "-quality", "quality", "-rc", "cqp", "-qp_i", q, "-qp_p", q],
            _ when encoder.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase) =>
                ["-c:v", encoder, "-global_quality", q],
            _ => ["-c:v", "libx264", "-preset", "veryfast", "-crf", q],
        };
        // Quick Sync only reads NV12, the same 4:2:0 picture in another memory layout.
        args.AddRange(["-pix_fmt", encoder.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase) ? "nv12" : "yuv420p"]);
        return args;
    }

    /// <summary>
    /// Saves the edit. With <paramref name="overwrite"/> the original is replaced only after the new file is complete,
    /// so a failure never damages the clip. Returns the path of the saved file.
    /// </summary>
    public static async Task<string> SaveAsync(AppConfig config, ClipMedia media, ClipEditPlan plan, bool overwrite,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var target = overwrite ? media.Path : NewFileName(media.Path);
        var directory = Path.GetDirectoryName(media.Path) ?? ".";
        // A .tmp name keeps the half-written file out of the clip library.
        var temp = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(media.Path)}.editando.tmp");
        // The first call probes the GPU encoder, so it stays off the UI thread.
        var encoder = !NeedsReencode(media, plan) ? "copy" : await Task.Run(() => EncoderSelector.Resolve(config), cancellationToken);
        var args = BuildArguments(config, media, plan, encoder, temp);
        Log.Info($"Salvando clipe editado: {Path.GetFileName(target)} ({Ffmpeg.Format(plan.Start)}s a {Ffmpeg.Format(plan.End)}s, " +
                 $"recorte {(plan.Crop is null ? "não" : "sim")}, vídeo {(NeedsReencode(media, plan) ? "recodificado" : "copiado")}, áudio {(plan.ChangesAudio || NeedsReencode(media, plan) ? "refeito" : "copiado")})");
        try
        {
            await RunWithProgressAsync(config.FfmpegExe, args, plan.End - EffectiveStart(media, plan), progress, cancellationToken);
            if (overwrite)
            {
                var originalTime = File.GetLastWriteTime(media.Path);
                await ReplaceAsync(temp, target);
                // Same clip, so it keeps its place in the library.
                File.SetLastWriteTime(target, originalTime);
            }
            else
            {
                File.Move(temp, target);
            }

            return target;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task ReplaceAsync(string source, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, target, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                // A video player or antivirus may still hold the original for a moment.
                await Task.Delay(300);
            }
        }
    }

    private static async Task RunWithProgressAsync(string ffmpeg, List<string> args, double seconds, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in args)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Não foi possível iniciar o ffmpeg.");
        PoPreyClip.Native.ChildProcessJob.Attach(process);
        try
        {
            // The game may still be open; the edit should never compete with it.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        });
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan("out_time_us=".Length), out var microseconds) && seconds > 0)
            {
                progress?.Report(Math.Clamp(microseconds / 1e6 / seconds, 0, 1));
            }
        }

        await process.WaitForExitAsync(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            var tail = string.Join(Environment.NewLine, (await errors).Split('\n').TakeLast(6));
            throw new InvalidOperationException($"O ffmpeg falhou (código {process.ExitCode}):{Environment.NewLine}{tail}");
        }

        progress?.Report(1);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Não foi possível apagar o arquivo temporário da edição: {ex.Message}");
        }
    }
}
