using System.Runtime.InteropServices;
using PoPreyClip.Audio;
using PoPreyClip.Config;

namespace PoPreyClip.Tests;

public class LimiterTests
{
    [Fact]
    public void Limiter_keeps_boosted_audio_under_the_ceiling()
    {
        var limiter = new PeakLimiter();
        var samples = new float[48_000 * 2];
        for (var i = 0; i < samples.Length; i += 2)
        {
            // A sine at 4x full scale, like a loud microphone boosted by +12 dB.
            var value = (float)(Math.Sin(i / 2 * 2 * Math.PI * 440 / 48_000) * 32767 * 4);
            samples[i] = value;
            samples[i + 1] = value;
        }

        limiter.Process(samples);

        Assert.All(samples, s => Assert.True(Math.Abs(s) <= PeakLimiter.Ceiling + 0.5f));
    }

    [Fact]
    public void Limiter_leaves_normal_audio_untouched()
    {
        var limiter = new PeakLimiter();
        float[] samples = [1000, -1000, 20000, -20000, 26000, 5];
        var copy = samples.ToArray();

        limiter.Process(samples);

        Assert.Equal(copy, samples);
        Assert.Equal(1f, limiter.Gain);
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(6, 1.995)]
    [InlineData(20, 10.0)]
    [InlineData(99, 31.62)]
    public void Decibels_convert_to_linear_gain(double decibels, double expected)
    {
        Assert.Equal(expected, AudioMath.GainFromDecibels(decibels), 2);
    }

    [Fact]
    public void Rms_of_full_scale_square_wave_is_one()
    {
        short[] square = [short.MaxValue, short.MinValue + 1, short.MaxValue, short.MinValue + 1];

        Assert.Equal(1.0, AudioMath.Rms(MemoryMarshal.AsBytes(square.AsSpan())), 3);
    }

    [Fact]
    public void Gain_is_clamped_and_new_microphones_get_a_boost()
    {
        var config = new AppConfig();
        config.Audio.Sources[0].GainDb = 80;
        config.Normalize();

        Assert.Equal(AudioSourceLimits.MaxGainDb, config.Audio.Sources[0].GainDb);
        Assert.Equal(10, config.Audio.Sources.Single(s => s.Id == AudioSourceIds.Microphone).GainDb);
    }
}
