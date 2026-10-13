using System.Globalization;
using System.Text.Json;
using PoPreyClip.Audio;
using PoPreyClip.Config;

namespace PoPreyClip.Recording;

/// <summary>
/// Re-balances a saved clip after the fact: mixes its separate source tracks with new volumes into a
/// new first track, and keeps the original tracks so the clip can be adjusted again later.
/// </summary>
internal static class Remixer
{
    public sealed record TrackInfo(int AudioIndex, string Title);

    public static async Task<List<TrackInfo>> ReadTracksAsync(AppConfig config, string input)
    {
        var result = await Ffmpeg.RunCheckedAsync(config.FfprobeExe,
            ["-v", "error", "-select_streams", "a", "-show_entries", "stream=index:stream_tags=title,handler_name", "-of", "json", input],
            "ffprobe");
        using var document = JsonDocument.Parse(result.StandardOutput);
        var tracks = new List<TrackInfo>();
        var audioIndex = 0;
        foreach (var stream in document.RootElement.GetProperty("streams").EnumerateArray())
        {
            string? title = null;
            if (stream.TryGetProperty("tags", out var tags))
            {
                title = tags.TryGetProperty("title", out var t) ? t.GetString() : null;
                title ??= tags.TryGetProperty("handler_name", out var h) ? h.GetString() : null;
            }

            tracks.Add(new TrackInfo(audioIndex, string.IsNullOrWhiteSpace(title) ? $"Faixa {audioIndex}" : title));
            audioIndex++;
        }

        return tracks;
    }

    /// <summary>Volume keys match a track title (case-insensitive) or its audio index.</summary>
    public static async Task<string> RemixAsync(AppConfig config, string input, string? output, IReadOnlyDictionary<string, int> volumes)
    {
        var tracks = await ReadTracksAsync(config, input);
        var sources = SelectSourceTracks(tracks);
        if (sources.Count == 0)
        {
            throw new InvalidOperationException("O clipe não tem faixas de áudio.");
        }

        output ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, Path.GetFileNameWithoutExtension(input) + "_mix.mp4");
        var filter = BuildFilter(sources, volumes);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y", "-i", input,
            "-filter_complex", filter,
            "-map", "0:v?", "-map", "[mix]",
        };
        foreach (var source in sources)
        {
            args.AddRange(["-map", $"0:a:{source.AudioIndex}"]);
        }

        args.AddRange(["-c:v", "copy", "-c:a", "copy", "-c:a:0", "aac", "-b:a:0", $"{config.Audio.AudioBitrateKbps}k"]);
        args.AddRange(["-metadata:s:a:0", $"title={AudioTrackPlan.MixTitle}", "-metadata:s:a:0", $"handler_name={AudioTrackPlan.MixTitle}", "-disposition:a:0", "default"]);
        for (var i = 0; i < sources.Count; i++)
        {
            args.AddRange([
                $"-metadata:s:a:{i + 1}", $"title={sources[i].Title}",
                $"-metadata:s:a:{i + 1}", $"handler_name={sources[i].Title}",
                $"-disposition:a:{i + 1}", "0",
            ]);
        }

        args.AddRange(["-movflags", "+faststart", output]);
        await Ffmpeg.RunCheckedAsync(config.FfmpegExe, args, "Remixar áudio");
        return output;
    }

    /// <summary>If the clip has a "Mix" track plus separate tracks, only the separate tracks are remixed.</summary>
    internal static List<TrackInfo> SelectSourceTracks(IReadOnlyList<TrackInfo> tracks)
    {
        if (tracks.Count > 1 && tracks[0].Title.Equals(AudioTrackPlan.MixTitle, StringComparison.OrdinalIgnoreCase))
        {
            return tracks.Skip(1).ToList();
        }

        return tracks.ToList();
    }

    internal static string BuildFilter(IReadOnlyList<TrackInfo> sources, IReadOnlyDictionary<string, int> volumes)
    {
        var parts = new List<string>();
        for (var i = 0; i < sources.Count; i++)
        {
            var volume = ResolveVolume(sources[i], volumes);
            parts.Add($"[0:a:{sources[i].AudioIndex}]volume={(volume / 100.0).ToString("0.###", CultureInfo.InvariantCulture)}[s{i}]");
        }

        var inputs = string.Concat(Enumerable.Range(0, sources.Count).Select(i => $"[s{i}]"));
        parts.Add($"{inputs}amix=inputs={sources.Count}:normalize=0:duration=longest[mix]");
        return string.Join(';', parts);
    }

    private static int ResolveVolume(TrackInfo track, IReadOnlyDictionary<string, int> volumes)
    {
        foreach (var (key, value) in volumes)
        {
            if (key.Equals(track.Title, StringComparison.OrdinalIgnoreCase) ||
                key == track.AudioIndex.ToString(CultureInfo.InvariantCulture))
            {
                return Math.Clamp(value, 0, 200);
            }
        }

        return 100;
    }
}
