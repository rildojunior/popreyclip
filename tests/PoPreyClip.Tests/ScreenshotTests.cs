using PoPreyClip.Clips;
using PoPreyClip.Config;
using PoPreyClip.Library;
using PoPreyClip.Recording;

namespace PoPreyClip.Tests;

public class ScreenshotTests
{
    [Fact]
    public void Screenshot_goes_to_the_game_screenshots_folder()
    {
        var path = ClipPathBuilder.BuildScreenshotPath(@"F:\Capturas", "Minecraft", new DateTime(2026, 10, 4, 21, 6, 34, 686), "PoPreyClip");

        Assert.Equal(@"F:\Capturas\PoPrey Game Capture\Minecraft\Screenshots\PoPreyClipMinecraft20261004210634686.png", path);
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
    public void Old_folders_move_into_the_game_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "popreyclip-old-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = new[]
            {
                Path.Combine(root, "Clips", "Palworld", "a.mp4"),
                Path.Combine(root, "Clips", "Palworld", "a-editado.mp4"),
                Path.Combine(root, "Clips", "Palworld", "a-editado-2.mp4"),
                Path.Combine(root, "Screenshots", "Palworld", "b.png"),
                Path.Combine(root, "Screenshots", "Steam", "notas.txt"),
            };
            foreach (var file in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "x");
            }

            var moved = ClipLibrary.MoveOldFolders(new AppConfig { ClipFolder = root });

            var game = Path.Combine(root, "PoPrey Game Capture", "Palworld");
            Assert.Equal(4, moved);
            Assert.True(File.Exists(Path.Combine(game, "a.mp4")));
            Assert.True(File.Exists(Path.Combine(game, "Edits", "a_edit.mp4")));
            Assert.True(File.Exists(Path.Combine(game, "Edits", "a_edit_2.mp4")));
            Assert.True(File.Exists(Path.Combine(game, "Screenshots", "b.png")));
            Assert.False(Directory.Exists(Path.Combine(root, "Clips")));
            // Files the app did not create stay where they are.
            Assert.True(File.Exists(Path.Combine(root, "Screenshots", "Steam", "notas.txt")));

            // Once PoPrey Game Capture exists, old folders are left alone.
            var late = Path.Combine(root, "Clips", "Palworld", "c.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(late)!);
            File.WriteAllText(late, "x");
            Assert.Equal(0, ClipLibrary.MoveOldFolders(new AppConfig { ClipFolder = root }));
            Assert.True(File.Exists(late));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Library_lists_clips_and_screenshots_together()
    {
        var root = Path.Combine(Path.GetTempPath(), "popreyclip-lib-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clip = Path.Combine(root, "PoPrey Game Capture", "Palworld", "a.mp4");
            var edit = Path.Combine(root, "PoPrey Game Capture", "Palworld", "Edits", "a_edit.mp4");
            var shot = Path.Combine(root, "PoPrey Game Capture", "Palworld", "Screenshots", "b.png");
            foreach (var file in new[] { clip, edit, shot })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "x");
            }

            var items = ClipLibrary.Scan(new AppConfig { ClipFolder = root });

            Assert.Equal(3, items.Count);
            Assert.All(items, i => Assert.Equal("Palworld", i.Game));
            Assert.Single(items, i => i.IsScreenshot && i.Path == shot);
            Assert.Single(items, i => !i.IsScreenshot && i.Path == clip);
            Assert.Single(items, i => !i.IsScreenshot && i.Path == edit);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
