using PoPreyClip.Config;
using PoPreyClip.Native;
using PoPreyClip.Recording;

namespace PoPreyClip.Tests;

public class CaptureSourceTests
{
    [Fact]
    public void Game_window_capture_is_the_default()
    {
        var config = new AppConfig();
        config.Normalize();

        Assert.Equal(VideoConfig.CaptureGameWindow, config.Video.Capture);
    }

    [Theory]
    [InlineData("MONITOR", VideoConfig.CaptureMonitor)]
    [InlineData("algo-estranho", VideoConfig.CaptureGameWindow)]
    [InlineData(null, VideoConfig.CaptureGameWindow)]
    public void Capture_setting_is_normalized(string? value, string expected)
    {
        var config = new AppConfig();
        config.Video.Capture = value!;
        config.Normalize();

        Assert.Equal(expected, config.Video.Capture);
    }

    [Fact]
    public void Window_source_films_only_that_window_with_an_even_size()
    {
        var config = new AppConfig();
        config.Normalize();

        var args = ReplayRecorder.BuildVideoArguments(config, "h264_nvenc", VideoSource.ForWindow(4660, "Jogo"), DateTimeOffset.UtcNow);

        Assert.Contains("gfxcapture=hwnd=4660:max_framerate=60:capture_cursor=0:width=-2:height=-2", args);
        Assert.DoesNotContain(args, a => a.StartsWith("ddagrab", StringComparison.Ordinal));
    }

    [Fact]
    public void Monitor_source_uses_desktop_duplication()
    {
        var config = new AppConfig();
        config.Normalize();
        config.Video.MonitorIndex = 1;

        var args = ReplayRecorder.BuildVideoArguments(config, "h264_nvenc", VideoSource.Monitor(1), DateTimeOffset.UtcNow);

        Assert.Contains("ddagrab=output_idx=1:framerate=60:draw_mouse=0", args);
    }

    [Fact]
    public void Recorder_reports_progress_so_a_frozen_capture_can_be_noticed()
    {
        var config = new AppConfig();
        config.Normalize();

        var args = ReplayRecorder.BuildVideoArguments(config, "h264_nvenc", VideoSource.Monitor(0), DateTimeOffset.UtcNow);

        Assert.Equal("pipe:1", args[args.IndexOf("-progress") + 1]);
    }

    [Fact]
    public void Clip_starts_at_the_decode_time_of_the_keyframe_just_before_the_wanted_point()
    {
        // ffprobe packet lines (pts_time,dts_time,flags) of the video buffer.
        const string packets = "1.450,1.400,K__\n3.487,3.437,K__\n4.000,3.950,___\n5.519,5.469,K__\n6.000,5.950,___\n7.567,7.517,K__\n";

        Assert.Equal(5.469 - 0.001, ClipExporter.KeyframeStart(packets, wanted: 6.5), precision: 6);
    }

    [Fact]
    public void Clip_start_uses_the_first_keyframe_when_none_is_early_enough()
    {
        const string packets = "3.487,3.437,K__\n4.000,3.950,___\n";

        Assert.Equal(3.437 - 0.001, ClipExporter.KeyframeStart(packets, wanted: 1.0), precision: 6);
        Assert.Equal(3.0, ClipExporter.KeyframeStart("", 3.0));
    }

    [Fact]
    public void Process_tree_includes_children_and_grandchildren_only()
    {
        List<Win32.ProcessInfo> processes =
        [
            new(10, 1, "launcher.exe"),
            new(20, 10, "game.exe"),
            new(30, 20, "crash-handler.exe"),
            new(40, 1, "discord.exe"),
        ];

        Assert.Equal([10, 20, 30], Win32.ProcessTree(processes, 10).Order());
    }
}
