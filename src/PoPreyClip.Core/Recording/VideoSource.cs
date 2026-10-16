using System.Collections.Concurrent;

namespace PoPreyClip.Recording;

/// <summary>What the replay buffer films: one window (the game) or the whole monitor.</summary>
internal readonly record struct VideoSource(IntPtr Window, string Description)
{
    public static VideoSource Monitor(int index) => new(IntPtr.Zero, $"monitor {index + 1}");

    public static VideoSource ForWindow(IntPtr window, string gameName) => new(window, $"janela de {gameName}");

    public bool IsWindow => Window != IntPtr.Zero;
}

/// <summary>Window capture needs the gfxcapture filter (FFmpeg 8 or newer). Checked once per ffmpeg.</summary>
internal static class WindowCaptureSupport
{
    private static readonly ConcurrentDictionary<string, bool> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsAvailable(string ffmpeg) => Cache.GetOrAdd(ffmpeg, static exe =>
    {
        try
        {
            var result = Ffmpeg.RunAsync(exe, ["-hide_banner", "-h", "filter=gfxcapture"]).GetAwaiter().GetResult();
            var available = result.ExitCode == 0 && result.StandardOutput.Contains("gfxcapture AVOptions", StringComparison.Ordinal);
            if (!available)
            {
                Log.Warn("Este FFmpeg não grava janelas (precisa da versão 8 ou mais nova). Usando a captura do monitor. Atualize com: winget upgrade Gyan.FFmpeg");
            }

            return available;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });
}
