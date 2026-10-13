using System.Diagnostics;
using System.Globalization;
using PoPreyClip.Clips;
using PoPreyClip.Config;

namespace PoPreyClip.Recording;

/// <summary>
/// Saves a lossless PNG of the recorded monitor to &lt;root&gt;\Screenshots\&lt;Game&gt;\, like Medal's
/// ScreenshotExporter. The frame comes straight from Desktop Duplication (the same source the recording
/// uses), so it is uncompressed and works whether or not recording is running.
/// </summary>
internal sealed class ScreenshotCapture
{
    /// <summary>Minimum time between two screenshots (Medal also ignores presses "on cooldown").</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _busy = new(1, 1);
    private DateTime _lastCapture = DateTime.MinValue;

    /// <summary>Returns the saved file, or null when skipped because of the cooldown.</summary>
    public async Task<string?> CaptureAsync(AppConfig config, string gameName)
    {
        if (DateTime.UtcNow - _lastCapture < Cooldown || !await _busy.WaitAsync(0))
        {
            Log.Debug("Captura de tela ignorada (intervalo mínimo).");
            return null;
        }

        try
        {
            _lastCapture = DateTime.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var target = ClipPathBuilder.BuildScreenshotPath(config.ClipFolder, gameName, DateTime.Now, config.FilePrefix);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await Ffmpeg.RunCheckedAsync(config.FfmpegExe, BuildArguments(config, target), "Captura de tela");
            if (!File.Exists(target))
            {
                throw new InvalidOperationException("O FFmpeg não gerou a imagem da captura de tela.");
            }

            Log.Info($"Captura de tela salva em {stopwatch.ElapsedMilliseconds} ms: {target}");
            return target;
        }
        finally
        {
            _busy.Release();
        }
    }

    internal static List<string> BuildArguments(AppConfig config, string target) =>
    [
        "-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i",
        $"ddagrab=output_idx={config.Video.MonitorIndex.ToString(CultureInfo.InvariantCulture)}:framerate=30:draw_mouse={(config.Video.ShowCursor ? 1 : 0)}",
        "-frames:v", "1", "-vf", "hwdownload,format=bgra,format=rgb24",
        // Level 3 gives the same file size as the default but encodes about 70 ms faster at 1440p.
        "-compression_level", "3",
        "-update", "1", target,
    ];
}
