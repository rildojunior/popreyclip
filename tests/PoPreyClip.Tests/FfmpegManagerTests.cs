using PoPreyClip.Updates;

namespace PoPreyClip.Tests;

public class FfmpegManagerTests
{
    [Fact]
    public void Picks_the_essentials_zip_with_its_checksum()
    {
        const string json = """
            {
              "tag_name": "9.0.2",
              "assets": [
                { "name": "ffmpeg-9.0.2-essentials_build.7z", "size": 35430500, "browser_download_url": "https://x/ess.7z", "digest": "sha256:aaa" },
                { "name": "ffmpeg-9.0.2-essentials_build.zip", "size": 114768076, "browser_download_url": "https://x/ess.zip", "digest": "sha256:60f4" },
                { "name": "ffmpeg-9.0.2-full_build.zip", "size": 257736868, "browser_download_url": "https://x/full.zip" }
              ]
            }
            """;

        var release = FfmpegManager.ParseRelease(json);

        Assert.Equal(new Version(9, 0, 2), release.Version);
        Assert.Equal("https://x/ess.zip", release.DownloadUrl);
        Assert.Equal(114768076, release.Size);
        Assert.Equal("60f4", release.Sha256);
    }

    [Fact]
    public void Release_without_the_essentials_zip_is_rejected()
    {
        const string json = """{ "tag_name": "9.0.2", "assets": [ { "name": "ffmpeg-9.0.2-full_build.7z", "size": 1, "browser_download_url": "https://x" } ] }""";

        Assert.Throws<InvalidDataException>(() => FfmpegManager.ParseRelease(json));
    }

    [Fact]
    public void Newest_complete_copy_comes_first()
    {
        var root = Path.Combine(Path.GetTempPath(), $"popreyclip-ffmpeg-{Guid.NewGuid():N}");
        try
        {
            CreateCopy(root, "8.0.1", complete: true);
            CreateCopy(root, "9.0.2", complete: true);
            CreateCopy(root, "10.0", complete: false);
            Directory.CreateDirectory(Path.Combine(root, "9.1.0.tmp"));

            var versions = FfmpegManager.InstalledVersions(root);

            Assert.Equal([new Version(9, 0, 2), new Version(8, 0, 1)], versions.Select(v => v.Version));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateCopy(string root, string version, bool complete)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, version)).FullName;
        File.WriteAllText(Path.Combine(directory, "ffmpeg.exe"), "");
        if (complete)
        {
            File.WriteAllText(Path.Combine(directory, "ffprobe.exe"), "");
        }
    }
}
