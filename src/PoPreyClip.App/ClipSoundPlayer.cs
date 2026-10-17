using System.Windows.Media;
using PoPreyClip.Config;

namespace PoPreyClip.App;

public sealed record ClipSoundInfo(string Id, string Label);

/// <summary>
/// Plays the "clip saved" and "screenshot saved" sounds. The built-in sounds are synthesized once and cached as
/// WAV files; a custom .wav or .mp3 can be used for the clip sound instead.
/// </summary>
public sealed class ClipSoundPlayer
{
    public const string CustomId = "custom";
    public const string DefaultClipId = "duo";
    public const string DefaultScreenshotId = "camera-soft";
    public const string ShutterId = "shutter";
    private const int SampleRate = 44_100;
    private const int Version = 3;

    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoPreyClip", "sounds");

    // Kept as a field so playback is not cut short by garbage collection.
    private readonly MediaPlayer _player = new();

    /// <summary>Sounds offered for a saved clip.</summary>
    public static IReadOnlyList<ClipSoundInfo> BuiltIn { get; } =
    [
        new(DefaultClipId, "Toque duplo"),
        new("octave", "Toque em oitava"),
        new("bright-bell", "Sino agudo"),
        new("chime", "Sino suave"),
        new("sparkle", "Brilho"),
        new("pop", "Bolha"),
        new("success", "Conquista"),
    ];

    /// <summary>Sounds offered for a saved screenshot.</summary>
    public static IReadOnlyList<ClipSoundInfo> ScreenshotSounds { get; } =
    [
        new(DefaultScreenshotId, "Câmera suave"),
        new("camera-bell", "Câmera com sino"),
        new(ShutterId, "Obturador clássico"),
    ];

    // The original sounds; the newer ones are drier and shorter.
    private static readonly HashSet<string> EchoedSounds = ["chime", "sparkle", "pop", "success", ShutterId];

    /// <summary>Plays the clip sound selected in the configuration. Must be called on the UI thread.</summary>
    public void Play(AppConfig config) => Play(config.ClipSound, config.ClipSoundFile, config.ClipSoundVolume);

    /// <summary>Plays the screenshot sound selected in the configuration, at the notification volume.</summary>
    public void PlayScreenshot(AppConfig config) => Play(config.ScreenshotSound, null, config.ClipSoundVolume);

    public void Play(string soundId, string? customFile, int volumePercent)
    {
        var file = soundId == CustomId ? customFile : EnsureBuiltIn(soundId);
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            file = EnsureBuiltIn(DefaultClipId);
        }

        try
        {
            _player.Stop();
            _player.Open(new Uri(file));
            _player.Volume = Math.Clamp(volumePercent, 0, 100) / 100.0;
            _player.Play();
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or InvalidOperationException)
        {
            Log.Warn($"Não foi possível tocar o som do clipe: {ex.Message}");
        }
    }

    private static string EnsureBuiltIn(string soundId)
    {
        if (BuiltIn.Concat(ScreenshotSounds).All(s => s.Id != soundId))
        {
            soundId = DefaultClipId;
        }

        var path = Path.Combine(CacheDirectory, $"{soundId}-v{Version}.wav");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllBytes(path, ToWav(Synthesize(soundId)));
        }

        return path;
    }

    internal static float[] Synthesize(string soundId)
    {
        var samples = new float[(int)(SampleRate * (EchoedSounds.Contains(soundId) ? 1.3 : 1.0))];
        switch (soundId)
        {
            case DefaultClipId:
                // Two soft glockenspiel strikes rising a fifth (A5, then E6). Short and dry.
                AddGlock(samples, 0.00, 880.00, 0.7, brightness: 0.5);
                AddGlock(samples, 0.13, 1318.51, 0.6, brightness: 0.5);
                break;

            case "octave":
                // The same soft strikes an octave apart (A5, then A6).
                AddGlock(samples, 0.00, 880.00, 0.7, brightness: 0.5);
                AddGlock(samples, 0.13, 1760.00, 0.55, brightness: 0.5);
                break;

            case "bright-bell":
                // Brighter, higher strikes an octave apart (C6, then C7).
                AddGlock(samples, 0.00, 1046.50, 0.75);
                AddGlock(samples, 0.14, 2093.00, 0.6);
                break;

            case DefaultScreenshotId:
                // One soft camera tick, then a gentle rising fourth (D5, G5).
                AddShutterTick(samples, 0.010, 0.4);
                AddSoftTone(samples, 0.06, 587.33, 0.35);
                AddSoftTone(samples, 0.11, 783.99, 0.65);
                break;

            case "camera-bell":
                // Two camera ticks, then a gentle rising fifth (C5, G5).
                AddShutterTick(samples, 0.010, 0.55);
                AddShutterTick(samples, 0.065, 0.45);
                AddSoftTone(samples, 0.10, 523.25, 0.35);
                AddSoftTone(samples, 0.15, 783.99, 0.7);
                break;

            case "sparkle":
                // Quick rising C major arpeggio, light and airy.
                double[] notes = [1046.50, 1318.51, 1567.98, 2093.00];
                for (var i = 0; i < notes.Length; i++)
                {
                    AddBell(samples, i * 0.06, notes[i], 0.7, 0.22, decay: 7.5);
                }

                break;

            case "pop":
                // Two soft "bloop" bubbles (pitch sweeps with fast decay).
                AddSweep(samples, 0, 380, 1100, 0.09, 0.55);
                AddSweep(samples, 0.10, 600, 1500, 0.08, 0.3);
                break;

            case "success":
                // Major arpeggio that rings out as a chord.
                AddBell(samples, 0.00, 783.99, 1.1, 0.26, decay: 4.5);
                AddBell(samples, 0.09, 1046.50, 1.0, 0.24, decay: 4.5);
                AddBell(samples, 0.18, 1318.51, 0.95, 0.24, decay: 4.2);
                break;

            case ShutterId:
                // Mechanical "click-clack": two short filtered noise bursts with a soft low thump.
                AddNoiseBurst(samples, 0.000, 0.028, 0.9, smoothing: 0.35);
                AddBell(samples, 0.000, 180, 0.08, 0.35, decay: 45);
                AddNoiseBurst(samples, 0.075, 0.035, 0.6, smoothing: 0.55);
                break;

            default:
                // "chime": two gentle bell notes a fifth apart (the original default).
                AddBell(samples, 0.00, 659.25, 1.0, 0.34, decay: 5.5);
                AddBell(samples, 0.10, 987.77, 1.1, 0.30, decay: 4.8);
                break;
        }

        if (EchoedSounds.Contains(soundId))
        {
            AddEcho(samples, delaySeconds: 0.045, feedback: 0.22);
        }

        Normalize(samples, 0.8f);
        return samples;
    }

    /// <summary>Bell-like tone: fundamental plus soft harmonics, 6 ms attack, exponential decay.</summary>
    private static void AddBell(float[] buffer, double start, double frequency, double duration, double amplitude, double decay)
    {
        var offset = (int)(start * SampleRate);
        var length = Math.Min((int)(duration * SampleRate), buffer.Length - offset);
        for (var i = 0; i < length; i++)
        {
            var t = i / (double)SampleRate;
            var envelope = Math.Min(1, t / 0.006) * Math.Exp(-t * decay) * FadeOut(i, length);
            var w = 2 * Math.PI * frequency * t;
            var tone = Math.Sin(w) + 0.22 * Math.Sin(2 * w) * Math.Exp(-t * 4) + 0.06 * Math.Sin(3.01 * w) * Math.Exp(-t * 8);
            buffer[offset + i] += (float)(tone * envelope * amplitude);
        }
    }

    /// <summary>
    /// Glockenspiel strike: a quick, bright attack that drops fast and leaves a quiet ring.
    /// <paramref name="brightness"/> scales the metallic upper partials.
    /// </summary>
    private static void AddGlock(float[] buffer, double start, double frequency, double amplitude, double brightness = 1)
    {
        var offset = (int)(start * SampleRate);
        var length = Math.Min((int)(0.8 * SampleRate), buffer.Length - offset);
        for (var i = 0; i < length; i++)
        {
            var t = i / (double)SampleRate;
            var w = 2 * Math.PI * frequency * t;
            var strike = Math.Sin(w) * Math.Exp(-t * 24) + 0.05 * Math.Sin(w) * Math.Exp(-t * 5);
            var metal = brightness * (0.12 * Math.Sin(4.007 * w) * Math.Exp(-t * 32) + 0.03 * Math.Sin(2.99 * w) * Math.Exp(-t * 40));
            var envelope = Math.Min(1, t / 0.0015) * FadeOut(i, length);
            buffer[offset + i] += (float)((strike + metal) * envelope * amplitude);
        }
    }

    /// <summary>Very short bright noise click, band-limited around 4 to 7 kHz, like a phone camera shutter.</summary>
    private static void AddShutterTick(float[] buffer, double start, double amplitude)
    {
        var random = new Random(77 + (int)(start * 1000));
        var offset = (int)(start * SampleRate);
        var length = Math.Min((int)(0.04 * SampleRate), buffer.Length - offset);
        double low = 0, lower = 0;
        for (var i = 0; i < length; i++)
        {
            var t = i / (double)SampleRate;
            low += (random.NextDouble() * 2 - 1 - low) * 0.6;
            lower += (low - lower) * 0.35;
            var envelope = Math.Min(1, t / 0.001) * Math.Exp(-t * 160);
            buffer[offset + i] += (float)((low - lower) * envelope * amplitude);
        }
    }

    /// <summary>Soft, round tone that rings for about half a second (sine plus a faint octave).</summary>
    private static void AddSoftTone(float[] buffer, double start, double frequency, double amplitude)
    {
        var offset = (int)(start * SampleRate);
        var length = Math.Min((int)(0.8 * SampleRate), buffer.Length - offset);
        for (var i = 0; i < length; i++)
        {
            var t = i / (double)SampleRate;
            var w = 2 * Math.PI * frequency * t;
            var envelope = Math.Min(1, t / 0.008) * Math.Exp(-t * 11) * FadeOut(i, length);
            buffer[offset + i] += (float)((Math.Sin(w) + 0.04 * Math.Sin(2 * w)) * envelope * amplitude);
        }
    }

    /// <summary>Exponential pitch sweep with a smooth fade, used for bubble sounds.</summary>
    private static void AddSweep(float[] buffer, double start, double from, double to, double duration, double amplitude)
    {
        var offset = (int)(start * SampleRate);
        var length = Math.Min((int)(duration * 2.2 * SampleRate), buffer.Length - offset);
        double phase = 0;
        for (var i = 0; i < length; i++)
        {
            var t = i / (double)SampleRate;
            var progress = Math.Min(1, t / duration);
            var frequency = from * Math.Pow(to / from, progress);
            phase += 2 * Math.PI * frequency / SampleRate;
            var envelope = Math.Min(1, t / 0.004) * Math.Exp(-t * 28) * FadeOut(i, length);
            buffer[offset + i] += (float)(Math.Sin(phase) * envelope * amplitude);
        }
    }

    /// <summary>Smooth 60 ms ramp to zero at the end of a note, so it never stops with a click.</summary>
    private static double FadeOut(int index, int length)
    {
        var fade = SampleRate * 0.06;
        var remaining = length - index;
        return remaining >= fade ? 1 : 0.5 - 0.5 * Math.Cos(Math.PI * remaining / fade);
    }

    /// <summary>Low-passed white noise with a sharp attack and exponential decay (mechanical click).</summary>
    private static void AddNoiseBurst(float[] buffer, double start, double duration, double amplitude, double smoothing)
    {
        var random = new Random(1234 + (int)(start * 1000));
        var offset = (int)(start * SampleRate);
        var length = Math.Min((int)(duration * 3 * SampleRate), buffer.Length - offset);
        double filtered = 0;
        for (var i = 0; i < length; i++)
        {
            var t = i / (double)SampleRate;
            filtered += (random.NextDouble() * 2 - 1 - filtered) * smoothing;
            var envelope = Math.Min(1, t / 0.0008) * Math.Exp(-t / (duration / 4)) * FadeOut(i, length);
            buffer[offset + i] += (float)(filtered * envelope * amplitude);
        }
    }

    private static void AddEcho(float[] buffer, double delaySeconds, double feedback)
    {
        var delay = (int)(delaySeconds * SampleRate);
        for (var i = delay; i < buffer.Length; i++)
        {
            buffer[i] += (float)(buffer[i - delay] * feedback);
        }
    }

    private static void Normalize(float[] buffer, float peak)
    {
        var max = buffer.Max(Math.Abs);
        if (max <= 0)
        {
            return;
        }

        var scale = peak / max;
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] *= scale;
        }

        // Short fade-out so the file never ends with a click.
        var fade = SampleRate / 20;
        for (var i = 0; i < fade; i++)
        {
            buffer[buffer.Length - 1 - i] *= i / (float)fade;
        }
    }

    internal static byte[] ToWav(float[] samples)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var dataLength = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataLength);
        foreach (var sample in samples)
        {
            writer.Write((short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue));
        }

        writer.Flush();
        return stream.ToArray();
    }
}
