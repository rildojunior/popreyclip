using PoPreyClip.Config;
using PoPreyClip.Hotkeys;
using PoPreyClip.Session;

namespace PoPreyClip.Tests;

public class SessionTests
{
    [Fact]
    public void Volume_change_does_not_require_restart()
    {
        var config = new AppConfig();
        var before = RecordingSession.Signature(config);

        config.Audio.Sources[0].Volume = 35;
        config.ClipSeconds = 90;
        config.ClipFolder = @"D:\Outra";

        Assert.Equal(before, RecordingSession.Signature(config));
    }

    [Fact]
    public void Track_layout_or_video_change_requires_restart()
    {
        var config = new AppConfig();
        var before = RecordingSession.Signature(config);

        config.Audio.Sources[2].Enabled = false;
        Assert.NotEqual(before, RecordingSession.Signature(config));

        config = new AppConfig { Audio = { MultipleAudioTracks = false } };
        Assert.NotEqual(before, RecordingSession.Signature(config));

        config = new AppConfig { Video = { Fps = 30 } };
        Assert.NotEqual(before, RecordingSession.Signature(config));
    }

    [Fact]
    public void Update_swaps_in_a_new_snapshot_and_saves_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"popreyclip-{Guid.NewGuid():N}.json");
        try
        {
            using var store = ConfigStore.LoadOrCreate(path);
            var original = store.Current;
            AppConfig? notified = null;
            store.Changed += c => notified = c;

            store.Update(c => c.Audio.Sources[1].Volume = 150);
            store.Flush();

            Assert.NotSame(original, store.Current);
            Assert.Equal(100, original.Audio.Sources[1].Volume);
            Assert.Equal(150, store.Current.Audio.Sources[1].Volume);
            Assert.Same(store.Current, notified);
            Assert.Equal(150, ConfigStore.Read(path).Audio.Sources[1].Volume);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Hotkeys_set_while_suspended_are_validated_but_not_registered()
    {
        using var hotkeys = new HotkeyManager();
        hotkeys.Suspend();

        Assert.NotNull(hotkeys.Set("save", "Ctrl+Banana", () => { }));
        Assert.Null(hotkeys.Set("save", "Ctrl+Alt+Shift+F23", () => { }));
        Assert.Null(hotkeys.Set("toggle", "", () => { }));
    }
}
