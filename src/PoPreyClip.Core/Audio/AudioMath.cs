using System.Runtime.InteropServices;
using PoPreyClip.Config;

namespace PoPreyClip.Audio;

public static class AudioMath
{
    /// <summary>Adds 16-bit PCM samples, multiplied by gain, into a float accumulator.</summary>
    public static void MixInto(Span<float> accumulator, ReadOnlySpan<byte> pcm16, float gain)
    {
        if (gain <= 0f)
        {
            return;
        }

        var samples = MemoryMarshal.Cast<byte, short>(pcm16);
        var count = Math.Min(samples.Length, accumulator.Length);
        for (var i = 0; i < count; i++)
        {
            accumulator[i] += samples[i] * gain;
        }
    }

    /// <summary>Converts the accumulator back to 16-bit PCM with hard clipping.</summary>
    public static void ToPcm16(ReadOnlySpan<float> accumulator, Span<byte> destination)
    {
        var samples = MemoryMarshal.Cast<byte, short>(destination);
        var count = Math.Min(samples.Length, accumulator.Length);
        for (var i = 0; i < count; i++)
        {
            samples[i] = (short)Math.Clamp(MathF.Round(accumulator[i]), short.MinValue, short.MaxValue);
        }
    }

    /// <summary>Peak absolute sample of 16-bit PCM, normalized to 0..1.</summary>
    public static float Peak(ReadOnlySpan<byte> pcm16)
    {
        var samples = MemoryMarshal.Cast<byte, short>(pcm16);
        var peak = 0;
        foreach (var sample in samples)
        {
            var magnitude = Math.Abs((int)sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return peak / 32768f;
    }

    /// <summary>Root-mean-square level of 16-bit PCM, normalized to 0..1 (closer to perceived loudness than the peak).</summary>
    public static float Rms(ReadOnlySpan<byte> pcm16)
    {
        var samples = MemoryMarshal.Cast<byte, short>(pcm16);
        if (samples.Length == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += (double)sample * sample;
        }

        return (float)(Math.Sqrt(sum / samples.Length) / 32768.0);
    }

    /// <summary>Converts a gain in decibels (clamped to 0..30 dB) into a linear factor.</summary>
    public static float GainFromDecibels(double decibels) =>
        (float)Math.Pow(10, Math.Clamp(decibels, 0, AudioSourceLimits.MaxGainDb) / 20.0);

    /// <summary>Converts a 0-200 percent volume into a linear gain; disabled sources get zero.</summary>
    public static float GainFromVolume(int volumePercent, bool enabled) =>
        enabled ? Math.Clamp(volumePercent, 0, 200) / 100f : 0f;
}
