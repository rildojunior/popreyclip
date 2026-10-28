using System.Text.Json.Serialization;

namespace PoPreyClip.Config;

/// <summary>
/// User configuration. Stored as JSON in %APPDATA%\PoPreyClip\config.json.
/// Audio volumes and enabled flags are re-read live while recording.
/// </summary>
public sealed class AppConfig
{
    public const string NoticeOverlay = "overlay";
    public const string NoticeWindows = "windows";
    public const string NoticeOff = "off";
    public const string DefaultClipSound = "duo";
    public const string DefaultScreenshotSound = "camera-soft";
    public const string DefaultToggleRecordingHotkey = "Alt+Shift+F9";
    private const int CurrentSoundDefaults = 2;
    private const int CurrentHotkeyDefaults = 1;
    private static readonly string[] OldToggleRecordingHotkeys = ["Ctrl+Alt+F8", "Alt+F9"];

    /// <summary>Root export folder. Clips go to &lt;ClipFolder&gt;\Clips\&lt;Game&gt;\.</summary>
    public string ClipFolder { get; set; } = @"C:\PoPreyClip";

    /// <summary>Length of each saved clip, in seconds.</summary>
    public int ClipSeconds { get; set; } = 30;

    /// <summary>Global hotkey that saves a clip, e.g. "F8" or "Ctrl+Shift+F9".</summary>
    public string Hotkey { get; set; } = "F8";

    /// <summary>Global hotkey that starts or stops recording. Empty disables it.</summary>
    public string ToggleRecordingHotkey { get; set; } = DefaultToggleRecordingHotkey;

    /// <summary>Global hotkey that saves a screenshot. Empty disables it.</summary>
    public string ScreenshotHotkey { get; set; } = "F12";

    /// <summary>
    /// Idle mode: recording turns on by itself when a game (or watched app) opens and off when it closes, so the
    /// app uses almost nothing while you are not playing. When false, <see cref="StartRecordingOnLaunch"/> applies.
    /// </summary>
    public bool AutoRecordGames { get; set; } = true;

    /// <summary>Desktop app: start recording as soon as the app opens.</summary>
    public bool StartRecordingOnLaunch { get; set; } = true;

    /// <summary>Desktop app: closing the window keeps PoPreyClip running in the notification area.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Desktop app: look for a new version on GitHub at start-up and every few hours.</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Desktop app: play a short sound when a clip is saved.</summary>
    public bool PlaySoundOnClip { get; set; } = true;

    /// <summary>Desktop app: built-in sound id ("duo", "octave", "chime", ...) or "custom".</summary>
    public string ClipSound { get; set; } = DefaultClipSound;

    /// <summary>Desktop app: .wav or .mp3 played when <see cref="ClipSound"/> is "custom".</summary>
    public string? ClipSoundFile { get; set; }

    /// <summary>Desktop app: notification volume, 0 to 100.</summary>
    public int ClipSoundVolume { get; set; } = 70;

    /// <summary>Desktop app: play a sound when a screenshot is saved.</summary>
    public bool PlaySoundOnScreenshot { get; set; } = true;

    /// <summary>Desktop app: built-in screenshot sound id ("camera-soft", "camera-bell" or "shutter").</summary>
    public string ScreenshotSound { get; set; } = DefaultScreenshotSound;

    /// <summary>
    /// Which built-in sound defaults this file has seen. Older files still on the previous default clip sound
    /// ("chime") move to the new one once; a sound picked later on purpose is kept.
    /// </summary>
    public int SoundDefaultsVersion { get; set; }

    /// <summary>
    /// Which hotkey defaults this file has seen. Older files whose start/stop hotkey is still "Ctrl+Alt+F8" (the
    /// old default) or "Alt+F9" (which clashes with saving a clip on F9 while Alt is held) move to
    /// <see cref="DefaultToggleRecordingHotkey"/> once; a hotkey picked later on purpose is kept.
    /// </summary>
    public int HotkeyDefaultsVersion { get; set; }

    /// <summary>
    /// Desktop app: how a saved clip or screenshot is announced while the app window is not in front.
    /// <see cref="NoticeOverlay"/> (PoPreyClip's own corner notice), <see cref="NoticeWindows"/> or <see cref="NoticeOff"/>.
    /// </summary>
    public string SavedNotice { get; set; } = NoticeOverlay;

    /// <summary>Desktop app: also copy each screenshot to the clipboard, like Medal.</summary>
    public bool CopyScreenshotToClipboard { get; set; } = true;

    /// <summary>File name prefix. Final name: &lt;prefix&gt;&lt;GameWithoutSpaces&gt;&lt;yyyyMMddHHmmssfff&gt;.mp4</summary>
    public string FilePrefix { get; set; } = "PoPreyClip";

    public string FfmpegPath { get; set; } = "ffmpeg";

    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>Resolved ffmpeg executable (searches PATH and the winget install folder).</summary>
    [JsonIgnore]
    public string FfmpegExe => ToolLocator.Resolve(FfmpegPath);

    /// <summary>Resolved ffprobe executable.</summary>
    [JsonIgnore]
    public string FfprobeExe => ToolLocator.Resolve(FfprobePath);

    public VideoConfig Video { get; set; } = new();

    public AudioConfig Audio { get; set; } = new();

    /// <summary>Desktop app: save a clip by saying the assistant's name and a command, e.g. "Oni, clipa isso".</summary>
    public VoiceConfig Voice { get; set; } = new();

    /// <summary>Treat any fullscreen or borderless foreground app as a game, even if it is not listed in <see cref="Games"/>.</summary>
    public bool DetectFullscreenApps { get; set; } = true;

    /// <summary>Executable name to game display name. The display name becomes the clip folder name.</summary>
    public Dictionary<string, string> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VALORANT-Win64-Shipping.exe"] = "VALORANT",
        ["cs2.exe"] = "Counter-Strike 2",
        ["FortniteClient-Win64-Shipping.exe"] = "Fortnite",
        ["r5apex.exe"] = "Apex Legends",
        ["RocketLeague.exe"] = "Rocket League",
        ["League of Legends.exe"] = "League of Legends",
        ["GTA5.exe"] = "Grand Theft Auto V",
        ["Palworld-Win64-Shipping.exe"] = "Palworld",
    };

    /// <summary>Foreground apps that are never treated as games by fullscreen detection.</summary>
    public List<string> IgnoredProcesses { get; set; } =
    [
        "explorer.exe", "PoPreyClip.exe", "Discord.exe", "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe",
        "brave.exe", "obs64.exe", "Medal.exe", "ApplicationFrameHost.exe", "ShellExperienceHost.exe",
        "SearchHost.exe", "StartMenuExperienceHost.exe", "LockApp.exe", "TextInputHost.exe", "Code.exe",
        "WindowsTerminal.exe", "steamwebhelper.exe", "Spotify.exe", "vlc.exe", "mpc-hc64.exe",
    ];

    /// <summary>Clamps values to safe ranges after loading.</summary>
    public void Normalize()
    {
        ClipSeconds = Math.Clamp(ClipSeconds, 5, 600);
        ClipSoundVolume = Math.Clamp(ClipSoundVolume, 0, 100);
        ClipSound = string.IsNullOrWhiteSpace(ClipSound) ? DefaultClipSound : ClipSound;
        ScreenshotSound = string.IsNullOrWhiteSpace(ScreenshotSound) ? DefaultScreenshotSound : ScreenshotSound;
        if (SoundDefaultsVersion < CurrentSoundDefaults)
        {
            if (ClipSound == "chime")
            {
                ClipSound = DefaultClipSound;
            }

            SoundDefaultsVersion = CurrentSoundDefaults;
        }
        var notice = SavedNotice?.Trim().ToLowerInvariant();
        SavedNotice = notice is NoticeWindows or NoticeOff ? notice : NoticeOverlay;
        Hotkey = string.IsNullOrWhiteSpace(Hotkey) ? "F8" : Hotkey.Trim();
        ToggleRecordingHotkey = ToggleRecordingHotkey?.Trim() ?? "";
        if (HotkeyDefaultsVersion < CurrentHotkeyDefaults)
        {
            if (OldToggleRecordingHotkeys.Contains(ToggleRecordingHotkey, StringComparer.OrdinalIgnoreCase))
            {
                ToggleRecordingHotkey = DefaultToggleRecordingHotkey;
            }

            HotkeyDefaultsVersion = CurrentHotkeyDefaults;
        }
        ScreenshotHotkey = ScreenshotHotkey?.Trim() ?? "";
        ClipFolder = string.IsNullOrWhiteSpace(ClipFolder) ? @"C:\PoPreyClip" : ClipFolder.Trim();
        FilePrefix ??= "";
        Video ??= new VideoConfig();
        Audio ??= new AudioConfig();
        Voice ??= new VoiceConfig();
        Voice.Normalize();
        Video.Capture = string.Equals(Video.Capture, VideoConfig.CaptureMonitor, StringComparison.OrdinalIgnoreCase)
            ? VideoConfig.CaptureMonitor
            : VideoConfig.CaptureGameWindow;
        Video.Fps = Math.Clamp(Video.Fps, 10, 240);
        VideoPresets.Normalize(Video);
        Video.SegmentSeconds = Math.Clamp(Video.SegmentSeconds, 1, 10);
        Video.Quality = Math.Clamp(Video.Quality, 1, 51);
        Audio.AudioBitrateKbps = Math.Clamp(Audio.AudioBitrateKbps, 64, 512);
        Games = new Dictionary<string, string>(Games ?? [], StringComparer.OrdinalIgnoreCase);
        IgnoredProcesses ??= [];
        Audio.Sources ??= [];
        foreach (var source in Audio.Sources)
        {
            source.Volume = Math.Clamp(source.Volume, 0, 200);
            source.GainDb = Math.Round(Math.Clamp(double.IsFinite(source.GainDb) ? source.GainDb : 0, 0, AudioSourceLimits.MaxGainDb), 1);
            source.Name = string.IsNullOrWhiteSpace(source.Name) ? source.Id : source.Name.Trim();
        }
    }
}

public sealed class VideoConfig
{
    public const string CaptureGameWindow = "game";
    public const string CaptureMonitor = "monitor";

    /// <summary>
    /// "game": only the game's window (Windows Graphics Capture), so other windows, alt-tab and the desktop never
    /// show up; the monitor is used when no game is open. "monitor": the whole monitor (Desktop Duplication).
    /// </summary>
    public string Capture { get; set; } = CaptureGameWindow;

    /// <summary>Quality preset like Medal's: <see cref="VideoPresets.Low"/>, Standard, High or Custom.</summary>
    public string Preset { get; set; } = VideoPresets.High;

    /// <summary>Clip height in pixels (2160, 1440, 1080, 720, 480 or 360). A smaller game or monitor is never enlarged.</summary>
    public int Resolution { get; set; } = 1080;

    /// <summary>Video bitrate in megabits per second (one of <see cref="VideoPresets.Bitrates"/>).</summary>
    public int BitrateMbps { get; set; } = 15;

    public int Fps { get; set; } = 60;

    /// <summary>Monitor index for Desktop Duplication capture (0 = primary output of the first GPU).</summary>
    public int MonitorIndex { get; set; }

    /// <summary>ffmpeg video encoder: "auto" (NVIDIA, AMD or Intel when available, otherwise CPU), h264_nvenc, hevc_nvenc, av1_nvenc, h264_amf, h264_qsv or libx264.</summary>
    public string Encoder { get; set; } = "auto";

    /// <summary>
    /// Constant quality value (CQ/CRF) used by the clip editor when it has to re-encode. Lower is better quality and
    /// bigger files. Recording uses <see cref="BitrateMbps"/>.
    /// </summary>
    public int Quality { get; set; } = 23;

    /// <summary>Length of each segment in the replay buffer.</summary>
    public int SegmentSeconds { get; set; } = 2;

    public bool ShowCursor { get; set; }
}

public sealed class AudioConfig
{
    /// <summary>When true each source is written to its own audio track (plus an optional mix track).</summary>
    public bool MultipleAudioTracks { get; set; } = true;

    /// <summary>Adds a first track with every source mixed, so ordinary players still play all audio.</summary>
    public bool IncludeMixTrack { get; set; } = true;

    public int AudioBitrateKbps { get; set; } = 160;

    /// <summary>Shows the source levels on the Audio page even when nothing is being recorded.</summary>
    public bool PreviewLevels { get; set; }

    /// <summary>
    /// Audio sources. Special ids: "game-audio" (the detected game process tree) and "microphone".
    /// Any other id is an executable name such as "Discord.exe" or "Spotify.exe".
    /// </summary>
    public List<AudioSourceConfig> Sources { get; set; } =
    [
        new() { Id = AudioSourceIds.GameAudio, Name = "Jogo" },
        new() { Id = AudioSourceIds.Microphone, Name = "Microfone", Device = "Auto", GainDb = 10 },
        new() { Id = "Discord.exe", Name = "Discord" },
    ];
}

public sealed class AudioSourceConfig
{
    public string Id { get; set; } = "";

    /// <summary>Track title written to the clip.</summary>
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>0 to 200 percent. Changes apply live.</summary>
    public int Volume { get; set; } = 100;

    /// <summary>Extra amplification in decibels (0 to 30), applied before the limiter. Useful for quiet microphones.</summary>
    public double GainDb { get; set; }

    /// <summary>Microphone only: "Auto" for the default device, or part of the device name.</summary>
    public string? Device { get; set; }
}

public sealed class VoiceConfig
{
    public const string SensitivityStrict = "strict";
    public const string SensitivityNormal = "normal";
    public const string SensitivityFlexible = "flexible";
    public const string DefaultAssistantName = "oni";
    public static readonly string[] DefaultCommands = ["clipa isso", "faz um clipe"];

    public bool Enabled { get; set; }

    /// <summary>
    /// Words said before every command, like Medal's "Medal clip that". A command said alone in a conversation does
    /// not save a clip.
    /// </summary>
    public List<string> AssistantNames { get; set; } = [DefaultAssistantName];

    public List<string> Commands { get; set; } = [.. DefaultCommands];

    /// <summary>
    /// Lowest recognition confidence that saves a clip: <see cref="SensitivityStrict"/> (high only),
    /// <see cref="SensitivityNormal"/> (medium and high) or <see cref="SensitivityFlexible"/> (low too).
    /// </summary>
    public string Sensitivity { get; set; } = SensitivityNormal;

    public void Normalize()
    {
        AssistantNames = Clean(AssistantNames);
        Commands = Clean(Commands);
        if (AssistantNames.Count == 0)
        {
            AssistantNames = [DefaultAssistantName];
        }

        if (Commands.Count == 0)
        {
            Commands = [.. DefaultCommands];
        }

        var sensitivity = Sensitivity?.Trim().ToLowerInvariant();
        Sensitivity = sensitivity is SensitivityStrict or SensitivityFlexible ? sensitivity : SensitivityNormal;
    }

    private static List<string> Clean(List<string>? items) =>
        (items ?? []).Select(PoPreyClip.Voice.VoicePhrases.Normalize).Where(t => t.Length > 0).Distinct().ToList();
}

public static class AudioSourceIds
{
    public const string GameAudio = "game-audio";
    public const string Microphone = "microphone";
}

public static class AudioSourceLimits
{
    public const double MaxGainDb = 30;
}
