using PoPreyClip.Config;
using PoPreyClip.Games;
using PoPreyClip.Native;

namespace PoPreyClip.Tests;

public class WatchedGamesTests
{
    [Fact]
    public void Idle_mode_is_on_by_default()
    {
        var config = new AppConfig();

        Assert.True(config.AutoRecordGames);
    }

    [Theory]
    [InlineData("SnippingTool.exe")]
    [InlineData("snippingtool.exe")]
    [InlineData("GameBar.exe")]
    public void Windows_screen_overlays_never_count_as_games(string exe)
    {
        // An old config file without them in the ignore list still ignores them.
        var config = new AppConfig { IgnoredProcesses = [] };

        Assert.True(GameDetector.IsIgnored(config, exe));
        Assert.False(GameDetector.IsIgnored(config, "Wardogs.exe"));
    }

    [Fact]
    public void Watched_app_is_detected_by_its_root_process_even_in_background()
    {
        var config = new AppConfig { Games = new(StringComparer.OrdinalIgnoreCase) { ["Emulador.exe"] = "Meu Emulador" } };
        List<Win32.ProcessInfo> processes =
        [
            new(10, 1, "explorer.exe"),
            new(500, 10, "emulador.exe"),
            new(501, 500, "Emulador.exe"),
        ];

        var game = GameDetector.FindWatchedProcess(config, processes);

        Assert.NotNull(game);
        Assert.Equal(500, game.ProcessId);
        Assert.Equal("Meu Emulador", game.DisplayName);
    }

    [Fact]
    public void Nothing_is_detected_when_no_watched_app_is_running()
    {
        var config = new AppConfig { Games = new(StringComparer.OrdinalIgnoreCase) { ["Emulador.exe"] = "Meu Emulador" } };

        Assert.Null(GameDetector.FindWatchedProcess(config, [new(10, 1, "explorer.exe")]));
    }
}
