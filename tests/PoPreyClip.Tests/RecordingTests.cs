using PoPreyClip.Config;
using PoPreyClip.Hotkeys;
using PoPreyClip.Native;
using PoPreyClip.Recording;

namespace PoPreyClip.Tests;

public class RecordingTests
{
    [Fact]
    public void Remix_skips_existing_mix_track_and_applies_volumes()
    {
        List<Remixer.TrackInfo> tracks = [new(0, "Mix"), new(1, "Jogo"), new(2, "Microfone"), new(3, "Discord")];
        var sources = Remixer.SelectSourceTracks(tracks);

        var filter = Remixer.BuildFilter(sources, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["discord"] = 40, ["1"] = 150 });

        Assert.Equal(
            "[0:a:1]volume=1.5[s0];[0:a:2]volume=1[s1];[0:a:3]volume=0.4[s2];[s0][s1][s2]amix=inputs=3:normalize=0:duration=longest[mix]",
            filter);
    }

    [Fact]
    public void Audio_process_maps_one_input_per_track_and_has_no_video()
    {
        var config = new AppConfig();
        config.Normalize();

        var args = ReplayRecorder.BuildAudioArguments(config, [@"\\.\pipe\a", @"\\.\pipe\b"]);

        Assert.Contains(@"\\.\pipe\a", args);
        Assert.Contains("0:a", args);
        Assert.Contains("1:a", args);
        Assert.Contains("segment", args);
        Assert.Contains("a%06d.ts", args);
        Assert.DoesNotContain(args, a => a.Contains("ddagrab") || a.Contains("gfxcapture"));
    }

    [Fact]
    public void Video_process_uses_wall_clock_shifted_to_the_first_audio_sample()
    {
        var config = new AppConfig();
        config.Normalize();
        var timeZero = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_250);

        var args = ReplayRecorder.BuildVideoArguments(config, "h264_nvenc", VideoSource.Monitor(0), timeZero);

        Assert.Equal("1", args[args.IndexOf("-use_wallclock_as_timestamps") + 1]);
        Assert.Contains("-copyts", args);
        Assert.Equal("-1760000000.250", args[args.IndexOf("-itsoffset") + 1]);
        Assert.True(args.IndexOf("-itsoffset") < args.IndexOf("-i"), "the offset must apply to the capture input");
        Assert.Equal("cfr", args[args.IndexOf("-fps_mode") + 1]);
        Assert.Contains("h264_nvenc", args);
        Assert.Contains("v%06d.ts", args);
        Assert.DoesNotContain(args, a => a.Contains("pipe", StringComparison.Ordinal) && a.StartsWith(@"\\", StringComparison.Ordinal));
    }

    [Fact]
    public void Automatic_encoder_is_the_default_and_falls_back_to_cpu_without_a_probe()
    {
        var config = new AppConfig();
        config.Normalize();

        var args = ReplayRecorder.BuildVideoArguments(config, null, VideoSource.Monitor(0), DateTimeOffset.UtcNow);

        Assert.Equal("auto", config.Video.Encoder);
        Assert.Contains("libx264", args);
        Assert.Contains("hwdownload,format=bgra,format=yuv420p", args);
    }

    [Fact]
    public void Amd_encoder_reads_the_captured_frames_directly_when_it_can()
    {
        var config = new AppConfig();
        config.Normalize();

        var direct = ReplayRecorder.BuildVideoArguments(config, "h264_amf", VideoSource.Monitor(0), DateTimeOffset.UtcNow, gpuFrames: true);
        var copied = ReplayRecorder.BuildVideoArguments(config, "h264_amf", VideoSource.Monitor(0), DateTimeOffset.UtcNow, gpuFrames: false);

        Assert.DoesNotContain("-vf", direct);
        Assert.Equal("hwdownload,format=bgra,format=nv12", copied[copied.IndexOf("-vf") + 1]);
        foreach (var args in new[] { direct, copied })
        {
            Assert.Equal("h264_amf", args[args.IndexOf("-c:v") + 1]);
            Assert.Equal("vbr_peak", args[args.IndexOf("-rc") + 1]);
            Assert.Equal("15000k", args[args.IndexOf("-b:v") + 1]);
            Assert.Equal(args[args.IndexOf("-g") + 1], args[args.IndexOf("-header_spacing") + 1]);
        }
    }

    [Fact]
    public void Intel_encoder_gets_nv12_frames()
    {
        var config = new AppConfig();
        config.Normalize();

        var args = ReplayRecorder.BuildVideoArguments(config, "h264_qsv", VideoSource.Monitor(0), DateTimeOffset.UtcNow, gpuFrames: true);

        Assert.Equal("hwdownload,format=bgra,format=nv12", args[args.IndexOf("-vf") + 1]);
        Assert.Equal("15000k", args[args.IndexOf("-b:v") + 1]);
        Assert.DoesNotContain("-global_quality", args);
    }

    [Theory]
    [InlineData("F8", 0u, 0x77u)]
    [InlineData("Ctrl+Shift+F9", HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x78u)]
    [InlineData("alt+c", HotkeyBinding.ModAlt, (uint)'C')]
    [InlineData("Ctrl+NumPad5", HotkeyBinding.ModControl, 0x65u)]
    public void Parses_hotkeys(string text, uint modifiers, uint key)
    {
        var binding = HotkeyBinding.Parse(text);

        Assert.Equal(modifiers, binding.Modifiers);
        Assert.Equal(key, binding.VirtualKey);
    }

    [Theory]
    [InlineData("F9", HotkeyBinding.ModShift, true)]
    [InlineData("F9", HotkeyBinding.ModControl | HotkeyBinding.ModShift, true)]
    [InlineData("F9", HotkeyBinding.ModAlt, true)]
    [InlineData("F9", HotkeyBinding.ModWin, false)]
    [InlineData("F9", HotkeyBinding.ModAlt | HotkeyBinding.ModWin, false)]
    [InlineData("Alt+F9", HotkeyBinding.ModAlt | HotkeyBinding.ModShift, true)]
    [InlineData("Alt+F9", HotkeyBinding.ModShift, false)]
    public void Accepts_hotkeys_with_shift_ctrl_or_alt_held(string text, uint heldModifiers, bool expected)
    {
        Assert.Equal(expected, HotkeyBinding.Parse(text).AcceptsLoosely(heldModifiers));
    }

    [Theory]
    [InlineData(null, AppConfig.NoticeOverlay)]
    [InlineData("banana", AppConfig.NoticeOverlay)]
    [InlineData(" Windows ", AppConfig.NoticeWindows)]
    [InlineData("off", AppConfig.NoticeOff)]
    public void Normalizes_saved_notice(string? value, string expected)
    {
        var config = new AppConfig { SavedNotice = value! };
        config.Normalize();

        Assert.Equal(expected, config.SavedNotice);
    }

    [Theory]
    [InlineData("chime", 0, AppConfig.DefaultClipSound)]
    [InlineData("chime", 2, "chime")]
    [InlineData("pop", 0, "pop")]
    [InlineData("", 0, AppConfig.DefaultClipSound)]
    public void Moves_old_default_clip_sound_to_the_new_one_once(string sound, int defaultsVersion, string expected)
    {
        var config = new AppConfig { ClipSound = sound, SoundDefaultsVersion = defaultsVersion, ScreenshotSound = "" };
        config.Normalize();

        Assert.Equal(expected, config.ClipSound);
        Assert.Equal(AppConfig.DefaultScreenshotSound, config.ScreenshotSound);
        Assert.Equal(2, config.SoundDefaultsVersion);
    }

    [Theory]
    [InlineData("Ctrl+Alt+F8", 0, AppConfig.DefaultToggleRecordingHotkey)]
    [InlineData("alt+f9", 0, AppConfig.DefaultToggleRecordingHotkey)]
    [InlineData("Alt+F9", 1, "Alt+F9")]
    [InlineData("Ctrl+F10", 0, "Ctrl+F10")]
    [InlineData("", 0, "")]
    public void Moves_old_toggle_recording_hotkey_to_the_new_default_once(string hotkey, int defaultsVersion, string expected)
    {
        var config = new AppConfig { ToggleRecordingHotkey = hotkey, HotkeyDefaultsVersion = defaultsVersion };
        config.Normalize();

        Assert.Equal(expected, config.ToggleRecordingHotkey);
        Assert.Equal(1, config.HotkeyDefaultsVersion);
    }

    [Fact]
    public void New_config_toggles_recording_with_alt_shift_f9()
    {
        var config = new AppConfig();
        config.Normalize();

        Assert.Equal("Alt+Shift+F9", config.ToggleRecordingHotkey);
    }

    [Theory]
    [InlineData("Ctrl")]
    [InlineData("F8+F9")]
    [InlineData("Ctrl+Banana")]
    public void Rejects_invalid_hotkeys(string text)
    {
        Assert.Throws<FormatException>(() => HotkeyBinding.Parse(text));
    }

    [Fact]
    public void Finds_root_process_of_multi_process_app()
    {
        List<Win32.ProcessInfo> processes =
        [
            new(10, 1, "explorer.exe"),
            new(200, 10, "Discord.exe"),
            new(201, 200, "Discord.exe"),
            new(202, 200, "discord.exe"),
        ];

        Assert.Equal(200, Win32.FindRootProcess(processes, "Discord.exe"));
        Assert.Null(Win32.FindRootProcess(processes, "Spotify.exe"));
    }

    [Fact]
    public void Config_round_trips_and_clamps_values()
    {
        var path = Path.Combine(Path.GetTempPath(), $"popreyclip-{Guid.NewGuid():N}.json");
        try
        {
            var config = new AppConfig { ClipSeconds = 9999 };
            config.Audio.Sources[2].Volume = 500;
            ConfigStore.Save(path, config);

            var loaded = ConfigStore.Read(path);

            Assert.Equal(600, loaded.ClipSeconds);
            Assert.Equal(200, loaded.Audio.Sources[2].Volume);
            Assert.True(loaded.Games.ContainsKey("CS2.EXE"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
