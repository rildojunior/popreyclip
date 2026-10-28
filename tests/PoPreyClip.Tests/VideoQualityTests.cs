using System.Text.Json;
using PoPreyClip.Config;
using PoPreyClip.Recording;

namespace PoPreyClip.Tests;

public class VideoQualityTests
{
    [Fact]
    public void High_quality_is_the_default()
    {
        var config = new AppConfig();
        config.Normalize();

        Assert.Equal(VideoPresets.High, config.Video.Preset);
        Assert.Equal(1080, config.Video.Resolution);
        Assert.Equal(60, config.Video.Fps);
        Assert.Equal(15, config.Video.BitrateMbps);
    }

    [Theory]
    [InlineData(VideoPresets.Low, "h264_nvenc", 360, 24, 3)]
    [InlineData(VideoPresets.Standard, "h264_nvenc", 720, 60, 10)]
    [InlineData(VideoPresets.High, "auto", 1080, 60, 15)]
    [InlineData(VideoPresets.Standard, "hevc_nvenc", 720, 60, 7)]
    [InlineData(VideoPresets.High, "av1_nvenc", 1080, 60, 10)]
    public void Presets_match_medal(string preset, string encoder, int resolution, int fps, int bitrate)
    {
        var config = new AppConfig();
        config.Video.Encoder = encoder;
        VideoPresets.Apply(config.Video, preset);
        config.Normalize();

        Assert.Equal(preset, config.Video.Preset);
        Assert.Equal(resolution, config.Video.Resolution);
        Assert.Equal(fps, config.Video.Fps);
        Assert.Equal(bitrate, config.Video.BitrateMbps);
    }

    [Fact]
    public void Changing_a_value_by_hand_turns_the_preset_into_custom()
    {
        var config = new AppConfig();
        config.Normalize();

        config.Video.BitrateMbps = 50;
        config.Normalize();

        Assert.Equal(VideoPresets.Custom, config.Video.Preset);
        Assert.Equal(50, config.Video.BitrateMbps);
    }

    [Fact]
    public void Odd_values_snap_to_the_lists()
    {
        var config = new AppConfig();
        config.Normalize();
        config.Video.Resolution = 1000;
        config.Video.BitrateMbps = 999;
        config.Normalize();

        Assert.Equal(1080, config.Video.Resolution);
        Assert.Equal(100, config.Video.BitrateMbps);
    }

    [Fact]
    public void Old_config_moves_to_high_quality_and_keeps_its_frame_rate()
    {
        var old = """{"video":{"capture":"game","fps":144,"encoder":"auto","quality":20}}""";
        var config = JsonSerializer.Deserialize<AppConfig>(old, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        config.Normalize();

        Assert.Equal(1080, config.Video.Resolution);
        Assert.Equal(15, config.Video.BitrateMbps);
        Assert.Equal(144, config.Video.Fps);
        Assert.Equal(VideoPresets.Custom, config.Video.Preset);
    }

    [Theory]
    [InlineData(2560, 1440, 1080, 1920, 1080)]
    [InlineData(3440, 1440, 1080, 2580, 1080)]
    [InlineData(1920, 1080, 720, 1280, 720)]
    [InlineData(1366, 768, 720, 1280, 720)]
    public void Output_keeps_the_shape_and_an_even_size(int width, int height, int resolution, int outWidth, int outHeight)
    {
        Assert.Equal((outWidth, outHeight), ReplayRecorder.OutputSize(resolution, (width, height)));
    }

    [Theory]
    [InlineData(1920, 1080, 1080)]
    [InlineData(1920, 1080, 2160)]
    [InlineData(0, 0, 720)]
    public void Smaller_or_unknown_sources_are_never_enlarged(int width, int height, int resolution)
    {
        Assert.Null(ReplayRecorder.OutputSize(resolution, (width, height)));
    }

    [Fact]
    public void Window_capture_shrinks_on_the_video_card()
    {
        var config = new AppConfig();
        config.Normalize();

        var args = ReplayRecorder.BuildVideoArguments(config, "h264_nvenc", VideoSource.ForWindow(4660, "Jogo"), DateTimeOffset.UtcNow,
            sourceSize: (2560, 1440));

        Assert.Contains("gfxcapture=hwnd=4660:max_framerate=60:capture_cursor=0:width=1920:height=1080:resize_mode=scale_aspect", args);
        Assert.DoesNotContain("-vf", args);
    }

    [Fact]
    public void Monitor_capture_shrinks_on_the_copy()
    {
        var config = new AppConfig();
        config.Normalize();

        var args = ReplayRecorder.BuildVideoArguments(config, "h264_nvenc", VideoSource.Monitor(0), DateTimeOffset.UtcNow,
            sourceSize: (2560, 1440));

        Assert.Equal("hwdownload,format=bgra,scale=w=1920:h=1080:flags=fast_bilinear,format=nv12", args[args.IndexOf("-vf") + 1]);
    }

    [Fact]
    public void Monitor_already_small_enough_goes_straight_to_the_encoder()
    {
        var config = new AppConfig();
        config.Normalize();

        var args = ReplayRecorder.BuildVideoArguments(config, "h264_nvenc", VideoSource.Monitor(0), DateTimeOffset.UtcNow,
            sourceSize: (1920, 1080));

        Assert.DoesNotContain("-vf", args);
    }

    [Theory]
    [InlineData("h264_nvenc")]
    [InlineData("h264_amf")]
    [InlineData("h264_qsv")]
    [InlineData("libx264")]
    public void Every_encoder_records_at_the_chosen_bitrate(string encoder)
    {
        var config = new AppConfig();
        config.Normalize();
        config.Video.BitrateMbps = 25;

        var args = ReplayRecorder.BuildVideoArguments(config, encoder, VideoSource.Monitor(0), DateTimeOffset.UtcNow);

        Assert.Equal("25000k", args[args.IndexOf("-b:v") + 1]);
        Assert.Equal("37500k", args[args.IndexOf("-maxrate") + 1]);
        Assert.Equal("50000k", args[args.IndexOf("-bufsize") + 1]);
        Assert.DoesNotContain("-cq", args);
        Assert.DoesNotContain("-crf", args);
    }
}
