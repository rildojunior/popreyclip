namespace PoPreyClip.Audio;

/// <summary>
/// Simple stereo peak limiter applied to every output track, so boosted sources (a quiet microphone
/// raised by +15 dB, for example) and the sum of several sources never clip. Similar in purpose to the
/// FFmpeg "alimiter" stage the Medal recorder places before its output.
/// Attack is instant; the gain recovers smoothly over about 150 ms.
/// </summary>
public sealed class PeakLimiter
{
    /// <summary>Ceiling of about -1.5 dBFS in 16-bit sample units (headroom for AAC encoder overshoot).</summary>
    public const float Ceiling = 32767f * 0.84f;

    private readonly float _release;
    private float _gain = 1f;

    public PeakLimiter(int sampleRate = AudioFormat.SampleRate, double releaseMs = 150)
    {
        _release = (float)(1 - Math.Exp(-1.0 / (sampleRate * releaseMs / 1000.0)));
    }

    /// <summary>Current gain reduction factor (1 = untouched).</summary>
    public float Gain => _gain;

    /// <summary>Limits interleaved stereo samples in place.</summary>
    public void Process(Span<float> interleavedStereo)
    {
        for (var i = 0; i + 1 < interleavedStereo.Length; i += 2)
        {
            var peak = Math.Max(Math.Abs(interleavedStereo[i]), Math.Abs(interleavedStereo[i + 1]));
            var target = peak > Ceiling ? Ceiling / peak : 1f;
            _gain = target < _gain ? target : _gain + (target - _gain) * _release;
            interleavedStereo[i] *= _gain;
            interleavedStereo[i + 1] *= _gain;
        }
    }
}
