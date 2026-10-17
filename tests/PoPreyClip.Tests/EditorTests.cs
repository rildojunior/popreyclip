using PoPreyClip.Config;
using PoPreyClip.Editing;
using PoPreyClip.Playback;

namespace PoPreyClip.Tests;

public class EditorTests
{
    private const string StreamsJson = """
        {"streams":[
          {"codec_type":"video","width":2560,"height":1440,"avg_frame_rate":"60/1","r_frame_rate":"60/1","tags":{"handler_name":"VideoHandler"}},
          {"codec_type":"audio","tags":{"handler_name":"Mix"}},
          {"codec_type":"audio","tags":{"title":"Jogo","handler_name":"Jogo"}},
          {"codec_type":"audio","tags":{"handler_name":"SoundHandler"}}
        ],"format":{"duration":"30.272000","start_time":"0.050000"}}
        """;

    private const string PacketsCsv = "0.050000,K__\n0.066667,___\n2.050000,K__\n2.066667,___\n4.050000,K__\n";

    private static ClipMedia Media() => ClipMedia.Parse(@"C:\clips\a.mp4", StreamsJson, PacketsCsv);

    private static AppConfig Config()
    {
        var config = new AppConfig();
        config.Normalize();
        return config;
    }

    [Fact]
    public void Reads_size_rate_tracks_and_keyframes_from_ffprobe()
    {
        var media = Media();

        Assert.Equal((2560, 1440, 60.0, 30.272), (media.Width, media.Height, media.Fps, media.Duration));
        Assert.Equal(["Mix", "Jogo", "Faixa 3"], media.AudioTracks.Select(t => t.Title));
        Assert.True(media.HasMixTrack);
        Assert.Equal(["Jogo", "Faixa 3"], media.SourceTracks.Select(t => t.Title));
        // Keyframes count from the file start, like "-ss".
        Assert.Equal([0.0, 2.0, 4.0], media.Keyframes.Select(k => Math.Round(k, 3)));
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.9, 0.0)]
    [InlineData(2.0, 2.0)]
    [InlineData(3.5, 2.0)]
    [InlineData(10.0, 4.0)]
    public void Copy_starts_at_the_keyframe_before_the_chosen_start(double start, double expected)
    {
        Assert.Equal(expected, Media().KeyframeAtOrBefore(start), 3);
    }

    [Fact]
    public void Trim_starting_on_a_keyframe_copies_video_and_audio()
    {
        var plan = new ClipEditPlan(2.0, 12, null, [100, 100], MergeAudio: false);

        var args = ClipEditor.BuildArguments(Config(), Media(), plan, "copy", "out.tmp");

        Assert.Equal("2.001", args[args.IndexOf("-ss") + 1]);
        Assert.True(args.IndexOf("-ss") < args.IndexOf("-i"), "the seek must be an input option");
        Assert.Equal("12", args[args.IndexOf("-to") + 1]);
        Assert.Equal("copy", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("copy", args[args.IndexOf("-c:a") + 1]);
        Assert.DoesNotContain("-filter_complex", args);
        Assert.DoesNotContain("-vf", args);
        Assert.Equal("out.tmp", args[^1]);
    }

    [Fact]
    public void Start_between_keyframes_is_reencoded_to_begin_exactly_there()
    {
        var plan = new ClipEditPlan(3.5, 12, null, [100, 100], MergeAudio: false);

        var args = ClipEditor.BuildArguments(Config(), Media(), plan, "h264_nvenc", "out.tmp");

        Assert.True(ClipEditor.NeedsReencode(Media(), plan));
        Assert.Equal("3.5", args[args.IndexOf("-ss") + 1]);
        Assert.Equal("h264_nvenc", args[args.IndexOf("-c:v") + 1]);
        Assert.DoesNotContain("-vf", args);
        // The audio is cut at the same exact point, so it is encoded too.
        Assert.Equal("aac", args[args.IndexOf("-c:a") + 1]);
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(2.005, false)]
    [InlineData(2.05, true)]
    public void Only_a_start_off_the_keyframes_needs_reencoding(double start, bool expected)
    {
        Assert.Equal(expected, ClipEditor.NeedsReencode(Media(), new ClipEditPlan(start, 12, null, [100, 100], MergeAudio: false)));
    }

    [Fact]
    public void Crop_reencodes_from_the_exact_start_with_the_recording_encoder()
    {
        var plan = new ClipEditPlan(3.5, 12, new CropRect(874, 0, 810, 1440), [100, 100], MergeAudio: false);

        var args = ClipEditor.BuildArguments(Config(), Media(), plan, "h264_nvenc", "out.tmp");

        Assert.Equal("3.5", args[args.IndexOf("-ss") + 1]);
        Assert.Equal("crop=810:1440:874:0", args[args.IndexOf("-vf") + 1]);
        Assert.Equal("h264_nvenc", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("yuv420p", args[args.IndexOf("-pix_fmt") + 1]);
    }

    [Fact]
    public void Volume_change_rebuilds_the_mix_and_keeps_the_separate_tracks()
    {
        var plan = new ClipEditPlan(0, 10, null, [100, 150], MergeAudio: false);

        var args = ClipEditor.BuildArguments(Config(), Media(), plan, "copy", "out.tmp");

        var graph = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("[0:a:1]volume=1,asplit=2[m0][t0]", graph);
        Assert.Contains("[0:a:2]volume=1.5,asplit=2[m1][t1]", graph);
        Assert.Contains("[m0][m1]amix=inputs=2:normalize=0", graph);
        // The boosted track gets the limiter so it cannot clip.
        Assert.Contains("[t1]alimiter", graph);
        Assert.Equal(["[mix]", "[t0]", "[l1]"], args.Select((a, i) => (a, i)).Where(x => x.a == "-map" && args[x.i + 1].StartsWith('[')).Select(x => args[x.i + 1]));
        Assert.Contains("title=Mix", args);
        Assert.Contains("title=Faixa 3", args);
        Assert.Equal("copy", args[args.IndexOf("-c:v") + 1]);
    }

    [Fact]
    public void Merge_writes_a_single_mixed_track()
    {
        var plan = new ClipEditPlan(0, 10, null, [100, 100], MergeAudio: true);

        var args = ClipEditor.BuildArguments(Config(), Media(), plan, "copy", "out.tmp");

        var graph = args[args.IndexOf("-filter_complex") + 1];
        Assert.DoesNotContain("asplit", graph);
        Assert.Single(args, a => a == "[mix]");
        Assert.DoesNotContain(args, a => a.StartsWith("[t", StringComparison.Ordinal));
        Assert.Contains("title=Mix", args);
    }

    [Fact]
    public void Player_lays_the_tracks_side_by_side_for_live_mixing()
    {
        var media = Media();

        Assert.Equal(
            "[0:a:1]aresample=48000,aformat=sample_fmts=flt:channel_layouts=stereo[s0];" +
            "[0:a:2]aresample=48000,aformat=sample_fmts=flt:channel_layouts=stereo[s1];[s0][s1]amerge=inputs=2[a]",
            ClipPlayer.AudioGraph(media.SourceTracks));
        Assert.Equal("[0:a:0]aresample=48000,aformat=sample_fmts=flt:channel_layouts=stereo[a]",
            ClipPlayer.AudioGraph([new ClipAudioTrack(0, "Mix")]));
    }

    [Fact]
    public void New_file_name_never_overwrites_an_existing_clip()
    {
        var directory = Directory.CreateTempSubdirectory("popreyclip-edit-");
        try
        {
            var original = Path.Combine(directory.FullName, "Clip.mp4");
            Assert.Equal(Path.Combine(directory.FullName, "Clip-editado.mp4"), ClipEditor.NewFileName(original));
            File.WriteAllText(Path.Combine(directory.FullName, "Clip-editado.mp4"), "");
            Assert.Equal(Path.Combine(directory.FullName, "Clip-editado-2.mp4"), ClipEditor.NewFileName(original));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
