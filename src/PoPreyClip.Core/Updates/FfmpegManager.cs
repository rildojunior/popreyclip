using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using PoPreyClip.Config;
using PoPreyClip.Recording;

namespace PoPreyClip.Updates;

/// <summary>An FFmpeg build that can be downloaded: the "essentials" build by Gyan, published on GitHub.</summary>
public sealed record FfmpegRelease(Version Version, string DownloadUrl, long Size, string? Sha256);

/// <summary>
/// Keeps PoPreyClip's own copy of FFmpeg, so nobody has to install or update it by hand. The copy lives in
/// %LOCALAPPDATA%\PoPreyClip\ffmpeg\&lt;version&gt;\ (only ffmpeg.exe and ffprobe.exe). A new version goes into
/// its own folder, so a recording that is running keeps its files; new recordings use the newest folder and the
/// old ones are removed once nothing uses them. Until the first download finishes, an FFmpeg installed on the PC
/// (PATH or winget) is used.
/// </summary>
public static class FfmpegManager
{
    public const string Repository = "GyanD/codexffmpeg";
    private const string AssetSuffix = "-essentials_build.zip";
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Busy = new(1, 1);
    private static double? _progress;

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoPreyClip", "ffmpeg");

    /// <summary>Raised when a download starts, advances, ends or installs a new version.</summary>
    public static event Action? Changed;

    /// <summary>Raised after a new version is ready to use.</summary>
    public static event Action<Version>? Installed;

    /// <summary>Download progress from 0 to 1, or null when nothing is being downloaded.</summary>
    public static double? Progress => _progress;

    /// <summary>Newest installed copy, or null.</summary>
    public static Version? InstalledVersion => InstalledVersions(Root).Select(v => v.Version).FirstOrDefault();

    /// <summary>bin folders of the installed copies, newest first (used by <see cref="ToolLocator"/>).</summary>
    internal static IEnumerable<string> BinDirectories() => InstalledVersions(Root).Select(v => v.Directory);

    internal static List<(Version Version, string Directory)> InstalledVersions(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(root)
            .Select(d => (Ok: Version.TryParse(Path.GetFileName(d), out var v), Version: v!, Directory: d))
            .Where(x => x.Ok && File.Exists(Path.Combine(x.Directory, "ffmpeg.exe")) && File.Exists(Path.Combine(x.Directory, "ffprobe.exe")))
            .OrderByDescending(x => x.Version)
            .Select(x => (x.Version, x.Directory))
            .ToList();
    }

    /// <summary>Latest release on GitHub.</summary>
    public static async Task<FfmpegRelease> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken);
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    internal static FfmpegRelease ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var version))
        {
            throw new InvalidDataException($"Versão do FFmpeg desconhecida: {tag}");
        }

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            return new FfmpegRelease(
                version,
                asset.GetProperty("browser_download_url").GetString() ?? throw new InvalidDataException("Download sem endereço."),
                asset.GetProperty("size").GetInt64(),
                digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null);
        }

        throw new InvalidDataException($"A versão {tag} do FFmpeg não tem o pacote {AssetSuffix}.");
    }

    /// <summary>
    /// Installs the latest FFmpeg when there is no copy yet or a newer one exists. Returns the installed version,
    /// or null when nothing changed. Only one check runs at a time.
    /// </summary>
    public static async Task<Version?> UpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!await Busy.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        try
        {
            var latest = await GetLatestAsync(cancellationToken);
            if (InstalledVersion is { } installed && installed >= latest.Version)
            {
                RemoveOldVersions();
                return null;
            }

            Log.Info($"Baixando o FFmpeg {latest.Version} ({latest.Size / 1048576} MB)...");
            await InstallAsync(latest, cancellationToken);
            Log.Info($"FFmpeg {latest.Version} instalado. As próximas gravações já usam essa versão.");
            return latest.Version;
        }
        finally
        {
            SetProgress(null);
            Busy.Release();
        }
    }

    private static async Task InstallAsync(FfmpegRelease release, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);
        var zip = Path.Combine(Root, $"download-{release.Version}.zip");
        var staging = Path.Combine(Root, $"{release.Version}.tmp");
        var target = Path.Combine(Root, release.Version.ToString());
        try
        {
            SetProgress(0);
            await DownloadAsync(release, zip, cancellationToken);

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Directory.CreateDirectory(staging);
            using (var archive = ZipFile.OpenRead(zip))
            {
                foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
                {
                    var entry = archive.Entries.FirstOrDefault(e =>
                        e.FullName.EndsWith("/bin/" + name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"O pacote do FFmpeg não tem {name}.");
                    entry.ExtractToFile(Path.Combine(staging, name), overwrite: true);
                }
            }

            // Make sure it runs on this PC before switching to it.
            var check = await Ffmpeg.RunAsync(Path.Combine(staging, "ffmpeg.exe"), ["-hide_banner", "-version"], cancellationToken);
            if (check.ExitCode != 0)
            {
                throw new InvalidDataException("O FFmpeg baixado não abriu neste PC.");
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(staging, target);
        }
        finally
        {
            TryDelete(zip);
            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        ToolLocator.Reset();
        RemoveOldVersions();
        Changed?.Invoke();
        Installed?.Invoke(release.Version);
    }

    private static async Task DownloadAsync(FfmpegRelease release, string target, CancellationToken cancellationToken)
    {
        using (var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(target);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            var lastReport = 0.0;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
                var progress = release.Size > 0 ? Math.Min(1, total / (double)release.Size) : 0;
                if (progress - lastReport >= 0.01)
                {
                    lastReport = progress;
                    SetProgress(progress);
                }
            }
        }

        var length = new FileInfo(target).Length;
        if (release.Size > 0 && length != release.Size)
        {
            throw new InvalidDataException($"O download do FFmpeg veio incompleto ({length} de {release.Size} bytes).");
        }

        if (release.Sha256 is not null)
        {
            await using var downloaded = File.OpenRead(target);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, cancellationToken));
            if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("O FFmpeg baixado não confere com a assinatura publicada (SHA-256).");
            }
        }
    }

    /// <summary>Deletes every copy but the newest. A copy in use by a running recording is kept and retried later.</summary>
    public static void RemoveOldVersions()
    {
        foreach (var (_, directory) in InstalledVersions(Root).Skip(1))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // ffmpeg.exe of that version is still running; try again next time.
            }
        }

        foreach (var leftover in Directory.Exists(Root) ? Directory.GetFiles(Root, "download-*.zip") : [])
        {
            TryDelete(leftover);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void SetProgress(double? value)
    {
        _progress = value;
        Changed?.Invoke();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PoPreyClip", UpdateService.CurrentVersion.ToString()));
        return client;
    }
}
