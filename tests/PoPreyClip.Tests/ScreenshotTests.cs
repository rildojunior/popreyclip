using PoPreyClip.Clips;
using PoPreyClip.Config;
using PoPreyClip.Library;
using PoPreyClip.Recording;

namespace PoPreyClip.Tests;

public class ScreenshotTests
{
    [Fact]
    public void Screenshot_path_follows_the_medal_layout()
    {
        var path = ClipPathBuilder.BuildScreenshotPath(@"F:\Capturas", "Minecraft", new DateTime(2026, 10, 4, 21, 6, 34, 686), "PoPreyClip");

        Assert.Equal(@"F:\Capturas\Screenshots\Minecraft\PoPreyClipMinecraft20261004210634686.png", path);
    }

    [Fact]
    public void Screenshot_defaults_to_f12_and_grabs_one_lossless_frame()
    {
        var config = new AppConfig();
        config.Normalize();

        var args = ScreenshotCapture.BuildArguments(config, @"C:\x.png");

        Assert.Equal("F12", config.ScreenshotHotkey);
        Assert.Contains("1", args);
        Assert.Contains(args, a => a.StartsWith("ddagrab=output_idx=0", StringComparison.Ordinal));
        Assert.Equal(@"C:\x.png", args[^1]);
    }

    [Fact]
    public void Library_lists_clips_and_screenshots_together()
    {
        var root = Path.Combine(Path.GetTempPath(), "popreyclip-lib-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clip = Path.Combine(root, "Clips", "Palworld", "a.mp4");
            var shot = Path.Combine(root, "Screenshots", "Palworld", "b.png");
            Directory.CreateDirectory(Path.GetDirectoryName(clip)!);
            Directory.CreateDirectory(Path.GetDirectoryName(shot)!);
            File.WriteAllText(clip, "x");
            File.WriteAllText(shot, "y");

            var items = ClipLibrary.Scan(new AppConfig { ClipFolder = root });

            Assert.Equal(2, items.Count);
            Assert.All(items, i => Assert.Equal("Palworld", i.Game));
            Assert.Single(items, i => i.IsScreenshot && i.Path == shot);
            Assert.Single(items, i => !i.IsScreenshot && i.Path == clip);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
