using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PoPreyClip.Native;

/// <summary>
/// Keeps the recorder fed while a game uses 100% of the GPU. Without this, Windows schedules the game's
/// GPU work first and the capture gets a frame only now and then (clips look frozen at ~1 fps).
/// Same approach as OBS and Medal: raise the recorder's GPU scheduling priority class, plus CPU priority.
/// </summary>
internal static class RecorderPriority
{
    private const int GpuPriorityHigh = 4;
    private const int GpuPriorityRealtime = 5;

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);

    /// <param name="gpu">Also raise the GPU priority (for the process that captures and encodes video).</param>
    public static void Raise(Process process, bool gpu)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Debug($"Prioridade de CPU do ffmpeg não alterada: {ex.Message}");
        }

        if (!gpu)
        {
            return;
        }

        // A process that just started rejects the call (invalid parameter) for ~100 ms, so retry in the background.
        _ = Task.Run(async () =>
        {
            var status = 0;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    if (process.HasExited)
                    {
                        return;
                    }

                    // Realtime needs administrator rights; high works for everyone.
                    if (D3DKMTSetProcessSchedulingPriorityClass(process.Handle, GpuPriorityRealtime) == 0)
                    {
                        Log.Debug("Prioridade de GPU do ffmpeg: tempo real.");
                        return;
                    }

                    status = D3DKMTSetProcessSchedulingPriorityClass(process.Handle, GpuPriorityHigh);
                    if (status == 0)
                    {
                        Log.Debug("Prioridade de GPU do ffmpeg: alta.");
                        return;
                    }
                }
                catch (Exception ex) when (ex is EntryPointNotFoundException or InvalidOperationException)
                {
                    Log.Debug($"Prioridade de GPU indisponível: {ex.Message}");
                    return;
                }

                await Task.Delay(100);
            }

            Log.Warn($"Não foi possível aumentar a prioridade de GPU do ffmpeg (0x{status:X8}).");
        });
    }
}
