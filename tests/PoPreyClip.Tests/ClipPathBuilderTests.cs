using PoPreyClip.Clips;

namespace PoPreyClip.Tests;

public class ClipPathBuilderTests
{
    private static readonly DateTime Timestamp = new(2026, 10, 11, 20, 7, 29, 655);

    [Fact]
    public void Builds_one_folder_per_game_with_compact_file_name()
    {
        var path = ClipPathBuilder.BuildClipPath(@"F:\Capturas", "ARC Raiders", Timestamp, "PoPreyClip");

        Assert.Equal(@"F:\Capturas\Clips\ARC Raiders\PoPreyClipARCRaiders20261011200729655.mp4", path);
    }

    [Theory]
    [InlineData("Counter-Strike: 2", "Counter-Strike 2")]
    [InlineData("Halo™ Infinite®", "Halo Infinite")]
    [InlineData("  Spaced   Out  ", "Spaced Out")]
    [InlineData("Game...", "Game")]
    [InlineData("CON", "CON_")]
    [InlineData("", ClipPathBuilder.UnknownGame)]
    [InlineData("???", ClipPathBuilder.UnknownGame)]
    public void Sanitizes_folder_names(string input, string expected)
    {
        Assert.Equal(expected, ClipPathBuilder.SanitizeFolderName(input));
    }

    [Fact]
    public void Compact_name_keeps_letters_and_digits_only()
    {
        Assert.Equal("EASportsFC26", ClipPathBuilder.CompactName("EA Sports FC 26"));
        Assert.Equal("ScheduleI", ClipPathBuilder.CompactName("Schedule I"));
    }

    [Fact]
    public void Adds_suffix_when_file_already_exists()
    {
        var root = Path.Combine(Path.GetTempPath(), "popreyclip-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = ClipPathBuilder.BuildClipPath(root, "Palworld", Timestamp);
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            File.WriteAllText(first, "");

            var second = ClipPathBuilder.BuildClipPath(root, "Palworld", Timestamp);

            Assert.EndsWith("_1.mp4", second);
            Assert.Equal(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
