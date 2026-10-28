namespace PoPreyClip.Config;

/// <summary>
/// Ready-made recording settings for people who do not want to pick a resolution and bitrate, copied from Medal's
/// presets (Low 360p 24 FPS, Standard 720p 60 FPS, High 1080p 60 FPS). H.265 and AV1 need less bitrate for the same
/// picture, so those encoders get a lower bitrate, as Medal does.
/// </summary>
public static class VideoPresets
{
    public const string Low = "low";
    public const string Standard = "standard";
    public const string High = "high";
    public const string Custom = "custom";

    /// <summary>Clip heights offered in the Resolution list, biggest first.</summary>
    public static readonly IReadOnlyList<int> Resolutions = [2160, 1440, 1080, 720, 480, 360];

    /// <summary>Bitrates (Mbps) offered in the Bitrate list.</summary>
    public static readonly IReadOnlyList<int> Bitrates = [3, 5, 7, 10, 15, 20, 25, 30, 50, 70, 100];

    public static readonly IReadOnlyList<VideoPreset> All =
    [
        new(Low, 360, 24, 3, 3),
        new(Standard, 720, 60, 10, 7),
        new(High, 1080, 60, 15, 10),
    ];

    public static VideoPreset? Find(string? id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>H.265/HEVC and AV1 keep the same picture with less bitrate than H.264.</summary>
    public static bool IsEfficientEncoder(string? encoder) =>
        encoder is not null && (encoder.StartsWith("hevc", StringComparison.OrdinalIgnoreCase)
            || encoder.StartsWith("av1", StringComparison.OrdinalIgnoreCase));

    /// <summary>Copies a preset's resolution, FPS and bitrate (for the current encoder) into the settings.</summary>
    public static void Apply(VideoConfig video, string id)
    {
        var preset = Find(id);
        if (preset is null)
        {
            video.Preset = Custom;
            return;
        }

        video.Preset = preset.Id;
        video.Resolution = preset.Resolution;
        video.Fps = preset.Fps;
        video.BitrateMbps = preset.BitrateFor(video.Encoder);
    }

    internal static void Normalize(VideoConfig video)
    {
        video.Resolution = Nearest(Resolutions, video.Resolution);
        video.BitrateMbps = Nearest(Bitrates, video.BitrateMbps);
        // Values changed by hand (or in the file) are no longer the preset. Files from before the presets get High
        // Quality's resolution and bitrate as defaults, and a frame rate other than 60 makes them Custom.
        if (Find(video.Preset) is not { } preset || !preset.Matches(video))
        {
            video.Preset = Custom;
        }
    }

    private static int Nearest(IReadOnlyList<int> values, int value) =>
        values.OrderBy(v => Math.Abs(v - value)).ThenByDescending(v => v).First();
}

/// <param name="Bitrate">Mbps for H.264.</param>
/// <param name="EfficientBitrate">Mbps for H.265/HEVC and AV1.</param>
public sealed record VideoPreset(string Id, int Resolution, int Fps, int Bitrate, int EfficientBitrate)
{
    public int BitrateFor(string? encoder) => VideoPresets.IsEfficientEncoder(encoder) ? EfficientBitrate : Bitrate;

    public bool Matches(VideoConfig video) =>
        video.Resolution == Resolution && video.Fps == Fps && video.BitrateMbps == BitrateFor(video.Encoder);
}
