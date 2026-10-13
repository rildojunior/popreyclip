using System.Diagnostics;
using System.Globalization;
using PoPreyClip.Audio;
using PoPreyClip.Config;

namespace PoPreyClip.Recording;

/// <summary>
/// Continuous replay buffer: one ffmpeg process captures the screen (Desktop Duplication) plus one raw
/// PCM named pipe per audio track, and writes short MPEG-TS segments. Old segments are deleted, so
/// only the last few seconds are kept on disk until a clip is saved.
/// </summary>
internal sealed class ReplayRecorder : IDisposable
{
    private const string SegmentPattern = "seg*.ts";
    private static int _sessionCounter;
    private readonly AppConfig _config;
    private readonly Func<AppConfig> _live;
    private Process? _process;
    private StreamWriter? _ffmpegLog;

    /// <param name="config">Settings fixed for this recording (video, tracks).</param>
    /// <param name="live">Current settings, for values that may change while recording (clip length).</param>
    public ReplayRecorder(AppConfig config, Func<AppConfig>? live = null)
    {
        _config = config;
        _live = live ?? (() => config);
        BufferDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoPreyClip", "buffer",
            $"{Environment.ProcessId}-{Interlocked.Increment(ref _sessionCounter)}");
    }

    public string BufferDirectory { get; }

    /// <summary>Deletes buffers left behind by PoPreyClip instances that are no longer running.</summary>
    public static void CleanupStaleBuffers()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoPreyClip", "buffer");
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            var pidText = name.Split('-')[0];
            if (int.TryParse(pidText, out var pid) && (pid == Environment.ProcessId || IsPoPreyClipRunning(pid)))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug($"Buffer antigo em uso, mantido: {directory}");
            }
        }
    }

    private static bool IsPoPreyClipRunning(int pid)
    {
        var path = PoPreyClip.Native.Win32.GetProcessPath(pid);
        return path is not null && Path.GetFileNameWithoutExtension(path).StartsWith("PoPreyClip", StringComparison.OrdinalIgnoreCase);
    }

    public bool HasExited => _process is { HasExited: true };

    public int ExitCode => _process?.ExitCode ?? 0;

    public string FfmpegLogPath => Path.Combine(BufferDirectory, "ffmpeg.log");

    public void Start(IReadOnlyList<PipeAudioSink> audioTracks)
    {
        if (Directory.Exists(BufferDirectory))
        {
            Directory.Delete(BufferDirectory, recursive: true);
        }

        Directory.CreateDirectory(BufferDirectory);
        var info = new ProcessStartInfo(_config.FfmpegExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            WorkingDirectory = BufferDirectory,
        };
        foreach (var argument in BuildArguments(_config, audioTracks.Select(t => t.PipePath).ToList(), EncoderSelector.Resolve(_config)))
        {
            info.ArgumentList.Add(argument);
        }

        Log.Debug("ffmpeg " + string.Join(' ', info.ArgumentList));
        _ffmpegLog = new StreamWriter(FfmpegLogPath) { AutoFlush = true };
        _process = Process.Start(info) ?? throw new InvalidOperationException("Não foi possível iniciar o ffmpeg.");
        PoPreyClip.Native.ChildProcessJob.Attach(_process);
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (_ffmpegLog)
                {
                    _ffmpegLog.WriteLine(e.Data);
                }
            }
        };
        _process.BeginErrorReadLine();
    }

    internal static List<string> BuildArguments(AppConfig config, IReadOnlyList<string> audioPipes, string? encoderOverride = null)
    {
        var video = config.Video;
        var encoder = encoderOverride ?? (string.Equals(video.Encoder, EncoderSelector.Auto, StringComparison.OrdinalIgnoreCase) ? "libx264" : video.Encoder);
        var isNvenc = encoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase);
        var gop = video.Fps * video.SegmentSeconds;
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostats", "-y",
            "-f", "lavfi", "-i",
            $"ddagrab=output_idx={video.MonitorIndex}:framerate={video.Fps}:draw_mouse={(video.ShowCursor ? 1 : 0)}",
        };

        foreach (var pipe in audioPipes)
        {
            args.AddRange([
                "-thread_queue_size", "4096",
                "-f", AudioFormat.FfmpegSampleFormat, "-ar", AudioFormat.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", AudioFormat.Channels.ToString(CultureInfo.InvariantCulture), "-i", pipe,
            ]);
        }

        args.AddRange(["-map", "0:v"]);
        if (isNvenc)
        {
            // NVENC takes the D3D11 frames from Desktop Duplication directly, without a copy to system memory.
            args.AddRange(["-c:v", encoder, "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", video.Quality.ToString(CultureInfo.InvariantCulture), "-b:v", "0", "-forced-idr", "1"]);
        }
        else if (encoder.Equals("libx264", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["-vf", "hwdownload,format=bgra,format=yuv420p", "-c:v", "libx264", "-preset", "veryfast", "-crf", video.Quality.ToString(CultureInfo.InvariantCulture)]);
        }
        else
        {
            args.AddRange(["-vf", "hwdownload,format=bgra,format=nv12", "-c:v", encoder, "-qp_i", video.Quality.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-g", gop.ToString(CultureInfo.InvariantCulture), "-force_key_frames", $"expr:gte(t,n_forced*{video.SegmentSeconds})"]);

        for (var i = 0; i < audioPipes.Count; i++)
        {
            args.AddRange(["-map", $"{i + 1}:a"]);
        }

        if (audioPipes.Count > 0)
        {
            args.AddRange(["-c:a", "aac", "-b:a", $"{config.Audio.AudioBitrateKbps}k"]);
        }

        args.AddRange([
            "-max_muxing_queue_size", "2048",
            "-f", "segment", "-segment_time", video.SegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-segment_format", "mpegts", "-reset_timestamps", "0",
            "seg%06d.ts",
        ]);
        return args;
    }

    /// <summary>Newest segments covering at least <paramref name="seconds"/> (the last one may still be growing).</summary>
    public List<string> GetRecentSegments(int seconds)
    {
        var needed = (int)Math.Ceiling(seconds / (double)_config.Video.SegmentSeconds) + 2;
        return Directory.GetFiles(BufferDirectory, SegmentPattern)
            .Order(StringComparer.Ordinal)
            .TakeLast(needed)
            .Where(f => new FileInfo(f).Length > 0)
            .ToList();
    }

    /// <summary>Deletes segments that can no longer be part of a clip.</summary>
    public void Cleanup()
    {
        if (!Directory.Exists(BufferDirectory))
        {
            return;
        }

        var keep = (int)Math.Ceiling(Math.Max(_config.ClipSeconds, _live().ClipSeconds) / (double)_config.Video.SegmentSeconds) + 4;
        var segments = Directory.GetFiles(BufferDirectory, SegmentPattern).Order(StringComparer.Ordinal).ToList();
        foreach (var old in segments.Take(Math.Max(0, segments.Count - keep)))
        {
            try
            {
                File.Delete(old);
            }
            catch (IOException)
            {
                // Still being read by an export; try again on the next cleanup.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public string ReadFfmpegLogTail()
    {
        try
        {
            using var stream = new FileStream(FfmpegLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return string.Join(Environment.NewLine, reader.ReadToEnd().Split('\n').TakeLast(10));
        }
        catch (IOException)
        {
            return "";
        }
    }

    public void Dispose()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                _process.StandardInput.Write('q');
                _process.StandardInput.Flush();
            }
            catch (IOException)
            {
            }

            if (!_process.WaitForExit(5000))
            {
                _process.Kill(entireProcessTree: true);
            }
        }

        _process?.Dispose();
        if (_ffmpegLog is not null)
        {
            lock (_ffmpegLog)
            {
                _ffmpegLog.Dispose();
            }
        }

        try
        {
            Directory.Delete(BufferDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
