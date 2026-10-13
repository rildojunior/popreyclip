using System.Runtime.InteropServices;
using PoPreyClip.Audio;
using PoPreyClip.Config;

namespace PoPreyClip.Tests;

public class AudioTests
{
    [Fact]
    public void Fifo_reads_back_in_order_across_wraparound()
    {
        var fifo = new PcmFifo(8);
        fifo.Write([1, 2, 3, 4, 5, 6]);
        var first = new byte[4];
        Assert.Equal(4, fifo.Read(first));
        fifo.Write([7, 8, 9, 10, 11, 12]);

        var rest = new byte[8];
        Assert.Equal(8, fifo.Read(rest));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, first);
        Assert.Equal(new byte[] { 5, 6, 7, 8, 9, 10, 11, 12 }, rest);
    }

    [Fact]
    public void Fifo_drops_oldest_when_full()
    {
        var fifo = new PcmFifo(4);
        fifo.Write([1, 2, 3]);
        fifo.Write([4, 5, 6]);

        var buffer = new byte[4];
        Assert.Equal(4, fifo.Read(buffer));
        Assert.Equal(new byte[] { 3, 4, 5, 6 }, buffer);
    }

    [Fact]
    public void Mix_applies_gain_and_clips()
    {
        short[] a = [1000, -1000, 30000, -30000];
        short[] b = [500, 500, 10000, -10000];
        var accumulator = new float[4];

        AudioMath.MixInto(accumulator, MemoryMarshal.AsBytes(a.AsSpan()), 1f);
        AudioMath.MixInto(accumulator, MemoryMarshal.AsBytes(b.AsSpan()), 0.5f);
        var output = new byte[8];
        AudioMath.ToPcm16(accumulator, output);

        Assert.Equal(new short[] { 1250, -750, short.MaxValue, short.MinValue }, MemoryMarshal.Cast<byte, short>(output).ToArray());
    }

    [Theory]
    [InlineData(100, true, 1f)]
    [InlineData(50, true, 0.5f)]
    [InlineData(300, true, 2f)]
    [InlineData(100, false, 0f)]
    public void Volume_maps_to_gain(int volume, bool enabled, float expected)
    {
        Assert.Equal(expected, AudioMath.GainFromVolume(volume, enabled));
    }

    [Fact]
    public void Track_plan_puts_mix_first_then_one_track_per_source()
    {
        var sources = new AudioConfig().Sources;

        var tracks = AudioTrackPlan.Build(sources, new AudioConfig { MultipleAudioTracks = true, IncludeMixTrack = true });

        Assert.Equal(["Mix", "Jogo", "Microfone", "Discord"], tracks.Select(t => t.Title));
        Assert.Equal([0, 1, 2], tracks[0].SourceIndexes);
        Assert.Equal([2], tracks[3].SourceIndexes);
    }

    [Fact]
    public void Track_plan_single_track_mixes_everything()
    {
        var sources = new AudioConfig().Sources;

        var tracks = AudioTrackPlan.Build(sources, new AudioConfig { MultipleAudioTracks = false });

        var only = Assert.Single(tracks);
        Assert.Equal([0, 1, 2], only.SourceIndexes);
    }
}
