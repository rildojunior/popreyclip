using PoPreyClip.Config;

namespace PoPreyClip.Recording;

/// <summary>
/// Resolves the "auto" video encoder: the first video card encoder the PC can actually use (NVIDIA, then AMD, then
/// Intel), otherwise the CPU encoder (libx264), which works on every machine. The probes run once per app session.
/// </summary>
internal static class EncoderSelector
{
    public const string Auto = "auto";
    private const string Fallback = "libx264";
    private static readonly (string Encoder, string Card)[] Candidates =
        [("h264_nvenc", "NVIDIA"), ("h264_amf", "AMD"), ("h264_qsv", "Intel")];

    private static readonly object Gate = new();
    private static readonly Dictionary<string, bool> DirectInput = new(StringComparer.OrdinalIgnoreCase);
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

            var found = Candidates.FirstOrDefault(c => Probe(config.FfmpegExe, c.Encoder));
            _detected = found.Encoder ?? Fallback;
            Log.Info(found.Encoder is null
                ? "Codificador automático: nenhuma placa de vídeo compatível, usando o processador (libx264)."
                : $"Codificador automático: usando a placa {found.Card} ({found.Encoder}).");
            return _detected;
        }
    }

    /// <summary>
    /// Whether the encoder can take the captured frames straight from the video card. NVENC always can. AMD AMF can
    /// when the card it runs on is the one that draws the monitor (on some laptops it is not); otherwise the frames are
    /// copied to system memory first. Intel Quick Sync always gets the copy.
    /// </summary>
    public static bool TakesGpuFrames(AppConfig config, string encoder)
    {
        if (encoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!encoder.EndsWith("_amf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (Gate)
        {
            if (!DirectInput.TryGetValue(encoder, out var direct))
            {
                var monitor = $"ddagrab=output_idx={config.Video.MonitorIndex}:framerate=30";
                direct = Probe(config.FfmpegExe, encoder, monitor);
                DirectInput[encoder] = direct;
                Log.Info(direct
                    ? $"{encoder}: a imagem vai direto da captura para o codificador."
                    : $"{encoder}: a captura está em outra placa, a imagem é copiada antes de codificar.");
            }

            return direct;
        }
    }

    /// <summary>Encodes a few frames with the encoder; success means the driver and GPU support it.</summary>
    private static bool Probe(string ffmpeg, string encoder, string input = "color=c=black:s=256x256:r=30")
    {
        try
        {
            var result = Ffmpeg.RunAsync(ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", input,
                 "-frames:v", "5", "-c:v", encoder, "-f", "null", "-"]).GetAwaiter().GetResult();
            return result.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
