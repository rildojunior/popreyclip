using PoPreyClip.Config;

namespace PoPreyClip.Recording;

/// <summary>
/// Resolves the "auto" video encoder: uses NVIDIA NVENC when the PC can actually encode with it, otherwise the
/// CPU encoder (libx264), which works on every machine. The probe runs once per app session.
/// </summary>
internal static class EncoderSelector
{
    public const string Auto = "auto";
    private const string Fallback = "libx264";
    private static readonly object Gate = new();
    private static string? _detected;

    public static string Resolve(AppConfig config)
    {
        var configured = config.Video.Encoder;
        if (!string.Equals(configured, Auto, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        lock (Gate)
        {
            if (_detected is not null)
            {
                return _detected;
            }

            _detected = Probe(config.FfmpegExe, "h264_nvenc") ? "h264_nvenc" : Fallback;
            Log.Info(_detected == Fallback
                ? "Codificador automático: placa NVIDIA não disponível, usando o processador (libx264)."
                : "Codificador automático: usando a placa NVIDIA (h264_nvenc).");
            return _detected;
        }
    }

    /// <summary>Encodes a few blank frames with the encoder; success means the driver and GPU support it.</summary>
    private static bool Probe(string ffmpeg, string encoder)
    {
        try
        {
            var result = Ffmpeg.RunAsync(ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=256x256:r=30",
                 "-frames:v", "5", "-c:v", encoder, "-f", "null", "-"]).GetAwaiter().GetResult();
            return result.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
