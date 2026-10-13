using System.Runtime.InteropServices;
using PoPreyClip.Config;
using PoPreyClip.Games;
using PoPreyClip.Native;

namespace PoPreyClip.Audio;

/// <summary>What a source should capture right now: one process tree, or one microphone.</summary>
internal abstract record CaptureTarget(string Description)
{
    public sealed record Process(int ProcessId, string Description) : CaptureTarget(Description);

    public sealed record Device(string DeviceId, string Description) : CaptureTarget(Description);
}

/// <summary>
/// Keeps one configured audio source alive. On every tick it resolves the current target
/// (game PID, app root PID, or microphone) and restarts capture when the target changes or fails,
/// similar to how the Medal recorder regenerates audio capture when a relevant process starts or exits.
/// </summary>
internal sealed class AudioSourceRunner(AudioSourceConfig source, GameDetector games) : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    private WasapiCapture? _capture;
    private CaptureTarget? _target;
    private CaptureTarget? _failedTarget;
    private DateTime _failedAt;

    public string Id { get; } = source.Id;

    public string Name { get; } = source.Name;

    /// <summary>About two seconds of audio; the pacer drains it every 10 ms.</summary>
    public PcmFifo Fifo { get; } = new(AudioFormat.SampleRate * AudioFormat.BytesPerFrame * 2);

    public void Tick(IReadOnlyList<Win32.ProcessInfo> processes)
    {
        CaptureTarget? desired;
        try
        {
            desired = Resolve(processes);
        }
        catch (COMException ex)
        {
            Log.Warn($"[{Name}] Não foi possível listar dispositivos: {ex.Message}");
            return;
        }

        if (Equals(desired, _target) && _capture is { Faulted: false })
        {
            return;
        }

        if (Equals(desired, _target) && _capture is null && Equals(desired, _failedTarget) && DateTime.UtcNow - _failedAt < RetryDelay)
        {
            return;
        }

        Stop();
        _target = desired;
        if (desired is null)
        {
            return;
        }

        try
        {
            _capture = desired switch
            {
                CaptureTarget.Process p => AudioDevices.CreateProcessCapture(p.ProcessId, Fifo, desired.Description),
                CaptureTarget.Device d => AudioDevices.CreateMicrophoneCapture(d.DeviceId, Fifo, desired.Description),
                _ => throw new InvalidOperationException(),
            };
            _capture.Start();
            _failedTarget = null;
            Log.Info($"[{Name}] Capturando: {desired.Description}");
        }
        catch (Exception ex) when (ex is COMException or TimeoutException or InvalidCastException or AggregateException)
        {
            _capture?.Dispose();
            _capture = null;
            _failedTarget = desired;
            _failedAt = DateTime.UtcNow;
            Log.Warn($"[{Name}] Falha ao iniciar captura de {desired.Description}: {ex.GetBaseException().Message}");
        }
    }

    private CaptureTarget? Resolve(IReadOnlyList<Win32.ProcessInfo> processes)
    {
        if (Id.Equals(AudioSourceIds.GameAudio, StringComparison.OrdinalIgnoreCase))
        {
            var game = games.CurrentGame;
            return game is null ? null : new CaptureTarget.Process(game.ProcessId, $"{game.DisplayName} (PID {game.ProcessId})");
        }

        if (Id.Equals(AudioSourceIds.Microphone, StringComparison.OrdinalIgnoreCase))
        {
            var device = AudioDevices.ResolveMicrophone(source.Device);
            return device is null ? null : new CaptureTarget.Device(device.Id, device.Name);
        }

        var pid = Win32.FindRootProcess(processes, Id);
        return pid is null ? null : new CaptureTarget.Process(pid.Value, $"{Id} (PID {pid})");
    }

    private void Stop()
    {
        if (_capture is null)
        {
            return;
        }

        _capture.Dispose();
        _capture = null;
        Fifo.Clear();
        Log.Info($"[{Name}] Captura parada.");
    }

    public void Dispose() => Stop();
}
