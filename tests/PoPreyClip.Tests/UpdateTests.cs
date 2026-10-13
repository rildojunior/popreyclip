using PoPreyClip.Updates;

namespace PoPreyClip.Tests;

public class UpdateTests
{
    private const string Release = """
        {
          "tag_name": "v1.2.3",
          "name": "PoPreyClip 1.2.3",
          "body": "Novidades",
          "html_url": "https://github.com/rildojunior/popreyclip/releases/tag/v1.2.3",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "PoPreyClip.exe", "size": 1234, "browser_download_url": "https://example.test/PoPreyClip.exe" },
            { "name": "PoPreyClip.exe.sha256", "size": 64, "browser_download_url": "https://example.test/PoPreyClip.exe.sha256" }
          ]
        }
        """;

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3+22a56a1", 1, 2, 3)]
    [InlineData("1.2.3-beta", 1, 2, 3)]
    [InlineData("2.0", 2, 0, 0)]
    public void Parses_versions_from_tags(string text, int major, int minor, int build)
    {
        Assert.True(UpdateService.TryParseVersion(text, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Fact]
    public void Newer_versions_compare_correctly()
    {
        UpdateService.TryParseVersion("v1.0.10", out var newer);
        UpdateService.TryParseVersion("1.0.9", out var older);

        Assert.True(newer > older);
    }

    [Fact]
    public void Reads_release_with_exe_and_checksum()
    {
        var update = UpdateService.ParseRelease(Release);

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 2, 3), update.Version);
        Assert.Equal("https://example.test/PoPreyClip.exe", update.DownloadUrl);
        Assert.Equal(1234, update.Size);
        Assert.Equal("https://example.test/PoPreyClip.exe.sha256", update.Sha256Url);
    }

    [Theory]
    [InlineData("\"draft\": false", "\"draft\": true")]
    [InlineData("\"prerelease\": false", "\"prerelease\": true")]
    [InlineData("\"name\": \"PoPreyClip.exe\"", "\"name\": \"Outro.zip\"")]
    public void Ignores_drafts_prereleases_and_releases_without_the_exe(string from, string to)
    {
        Assert.Null(UpdateService.ParseRelease(Release.Replace(from, to)));
    }
}
