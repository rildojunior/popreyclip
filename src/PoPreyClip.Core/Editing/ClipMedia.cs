using System.Globalization;
using System.Text.Json;
using PoPreyClip.Audio;
using PoPreyClip.Config;
using PoPreyClip.Recording;

namespace PoPreyClip.Editing;

/// <summary>An audio track of a clip. <see cref="AudioIndex"/> is the position among the audio streams (0:a:N).</summary>
public sealed record ClipAudioTrack(int AudioIndex, string Title);

/// <summary>What the editor needs to know about a saved clip, read once with ffprobe when the editor opens.</summary>
public sealed record ClipMedia(
    string Path,
    double Duration,
    int Width,
    int Height,
    double Fps,
    IReadOnlyList<ClipAudioTrack> AudioTracks,
    IReadOnlyList<double> Keyframes)
{
    /// <summary>
    /// Tracks the user adjusts: the separate source tracks when the clip also has a "Mix" track (the mix is rebuilt
    /// from them), otherwise every track.
    /// </summary>
    public IReadOnlyList<ClipAudioTrack> SourceTracks =>
        HasMixTrack ? AudioTracks.Skip(1).ToList() : AudioTracks;

    public bool HasMixTrack =>
        AudioTracks.Count > 1 && AudioTracks[0].Title.Equals(AudioTrackPlan.MixTitle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where a copy without re-encoding really starts: the last keyframe at or before <paramref name="start"/>.
    /// </summary>
    public double KeyframeAtOrBefore(double start)
    {
        var result = 0.0;
        foreach (var keyframe in Keyframes)
        {
            if (keyframe > start + 0.0005)
            {
                break;
            }

            result = keyframe;
        }

        return result;
    }

    public static async Task<ClipMedia> ProbeAsync(AppConfig config, string path)
    {
        var streams = Ffmpeg.RunCheckedAsync(config.FfprobeExe,
            ["-v", "error", "-show_entries", "format=duration,start_time:stream=codec_type,width,height,avg_frame_rate,r_frame_rate:stream_tags=title,handler_name",
             "-of", "json", path],
            "ffprobe");
        // Packet flags only (no decoding), so this stays fast even for long clips.
        var packets = Ffmpeg.RunCheckedAsync(config.FfprobeExe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", path],
            "ffprobe");
        return Parse(path, (await streams).StandardOutput, (await packets).StandardOutput);
    }

    internal static ClipMedia Parse(string path, string streamsJson, string packetsCsv)
    {
        using var document = JsonDocument.Parse(streamsJson);
        var root = document.RootElement;
        double duration = 0, startTime = 0;
        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var d))
            {
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            }

            if (format.TryGetProperty("start_time", out var st))
            {
                double.TryParse(st.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out startTime);
            }
        }

        int width = 0, height = 0;
        double fps = 0;
        var tracks = new List<ClipAudioTrack>();
        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
            if (type == "video" && width == 0)
            {
                width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                fps = ParseRate(stream, "avg_frame_rate") is > 0 and var avg ? avg : ParseRate(stream, "r_frame_rate");
            }
            else if (type == "audio")
            {
                string? title = null;
                if (stream.TryGetProperty("tags", out var tags))
                {
                    title = tags.TryGetProperty("title", out var tt) ? tt.GetString() : null;
                    // MP4 keeps the track name in the handler name; "SoundHandler" is only ffmpeg's default.
                    if (string.IsNullOrWhiteSpace(title) && tags.TryGetProperty("handler_name", out var handler) &&
                        handler.GetString() is { } name && !name.Equals("SoundHandler", StringComparison.OrdinalIgnoreCase))
                    {
                        title = name;
                    }
                }

                var index = tracks.Count;
                tracks.Add(new ClipAudioTrack(index, string.IsNullOrWhiteSpace(title) ? (index == 0 ? "Áudio" : $"Faixa {index + 1}") : title));
            }
        }

        var keyframes = new List<double>();
        foreach (var line in packetsCsv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length >= 2 && parts[1].Contains('K') &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
            {
                keyframes.Add(time);
            }
        }

        keyframes.Sort();
        // Packet times count from the file's start time, while "-ss" counts from the start of the file.
        return new ClipMedia(path, duration, width, height, fps > 0 ? fps : 60, tracks,
            keyframes.Select(k => Math.Max(0, k - startTime)).ToList());
    }

    private static double ParseRate(JsonElement stream, string name)
    {
        if (!stream.TryGetProperty(name, out var value) || value.GetString() is not { } text)
        {
            return 0;
        }

        var parts = text.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && den > 0)
        {
            return num / den;
        }

        return 0;
    }
}
