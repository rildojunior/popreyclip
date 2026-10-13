using PoPreyClip.Audio.Interop;

namespace PoPreyClip.Audio;

/// <summary>Every source is captured and every track is written as 48 kHz, stereo, signed 16-bit PCM.</summary>
public static class AudioFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BitsPerSample = 16;
    public const int BytesPerFrame = Channels * BitsPerSample / 8;
    public const string FfmpegSampleFormat = "s16le";

    internal static WaveFormatEx ToWaveFormat() => new()
    {
        FormatTag = 1, // WAVE_FORMAT_PCM
        Channels = Channels,
        SamplesPerSec = SampleRate,
        BitsPerSample = BitsPerSample,
        BlockAlign = BytesPerFrame,
        AvgBytesPerSec = SampleRate * BytesPerFrame,
        ExtraSize = 0,
    };
}
