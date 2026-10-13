using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace PoPreyClip.Updates;

/// <summary>A release newer than the running app.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Title, string Notes, string DownloadUrl, long Size, string? Sha256Url, string PageUrl);

/// <summary>
/// Self-update through GitHub Releases. Checks the latest release of the public repository, downloads its
/// PoPreyClip.exe asset, verifies size and SHA-256, and swaps it in for the running executable.
/// Windows cannot overwrite a running .exe but can rename it, so the swap is: current → .old, new → current,
/// start the new one (it waits for this process to exit), then exit. The next start deletes the .old file.
/// </summary>
public sealed class UpdateService
{
    public const string Repository = "rildojunior/popreyclip";
    public const string AssetName = "PoPreyClip.exe";
    public const string ChecksumAssetName = "PoPreyClip.exe.sha256";

    /// <summary>For tests: path to a local JSON file shaped like the GitHub "latest release" response.</summary>
    public const string FeedOverrideVariable = "POPREYCLIP_UPDATE_FEED";

    private static readonly HttpClient Http = CreateClient();

    public static string ReleasesPage => $"https://github.com/{Repository}/releases/latest";

    private static string UpdatesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoPreyClip", "updates");

    /// <summary>Version of the running app (from the build's informational version, without the "+commit" suffix).</summary>
    public static Version CurrentVersion
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return TryParseVersion(informational, out var version) ? version : assembly.GetName().Version ?? new Version(0, 0, 0);
        }
    }

    /// <summary>Accepts "v1.2.3", "1.2.3", "1.2.3+abc" and "1.2.3-beta" (pre-release suffix ignored).</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var core = text.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        if (!Version.TryParse(core, out var parsed))
        {
            return false;
        }

        version = new Version(parsed.Major, Math.Max(parsed.Minor, 0), Math.Max(parsed.Build, 0));
        return true;
    }

    /// <summary>
    /// True when the app is the published single-file .exe in a folder the user can write to.
    /// Development builds (dotnet run) and read-only folders fall back to opening the download page.
    /// </summary>
    public static bool CanSelfUpdate
    {
        get
        {
            var exe = Environment.ProcessPath;
            var isSingleFile = string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location);
            if (exe is null || !isSingleFile)
            {
                return false;
            }

            try
            {
                var probe = Path.Combine(Path.GetDirectoryName(exe)!, $".popreyclip-write-test-{Environment.ProcessId}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Returns the latest release when it is newer than the running app, otherwise null.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var json = await ReadFeedAsync(cancellationToken);
        var latest = ParseRelease(json);
        if (latest is null || latest.Version <= CurrentVersion)
        {
            return null;
        }

        return latest;
    }

    internal static UpdateInfo? ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
            root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
        {
            return null;
        }

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version))
        {
            return null;
        }

        string? downloadUrl = null;
        string? checksumUrl = null;
        long size = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
            {
                downloadUrl = url;
                size = asset.GetProperty("size").GetInt64();
            }
            else if (string.Equals(name, ChecksumAssetName, StringComparison.OrdinalIgnoreCase))
            {
                checksumUrl = url;
            }
        }

        if (downloadUrl is null)
        {
            return null;
        }

        return new UpdateInfo(
            version,
            tag,
            root.TryGetProperty("name", out var title) ? title.GetString() ?? tag : tag,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            downloadUrl,
            size,
            checksumUrl,
            root.TryGetProperty("html_url", out var page) ? page.GetString() ?? ReleasesPage : ReleasesPage);
    }

    /// <summary>Downloads the new .exe and verifies it. Returns the path of the verified file.</summary>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(UpdatesDirectory);
        var target = Path.Combine(UpdatesDirectory, $"PoPreyClip-{update.Version}.exe");
        var partial = target + ".part";

        await using (var source = await OpenAsync(update.DownloadUrl, cancellationToken))
        await using (var output = File.Create(partial))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
                if (update.Size > 0)
                {
                    progress?.Report(Math.Min(1, total / (double)update.Size));
                }
            }
        }

        var length = new FileInfo(partial).Length;
        if (update.Size > 0 && length != update.Size)
        {
            File.Delete(partial);
            throw new InvalidDataException($"O download veio incompleto ({length} de {update.Size} bytes).");
        }

        if (update.Sha256Url is not null)
        {
            var expected = await ReadChecksumAsync(update.Sha256Url, cancellationToken);
            string actual;
            await using (var downloaded = File.OpenRead(partial))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, cancellationToken));
            }

            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidDataException("O arquivo baixado não confere com a assinatura da versão (SHA-256).");
            }
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// Step 1 (running app): starts the downloaded .exe as the installer. The caller must exit normally right after.
    /// The running app never touches its own file: a single-file .exe keeps reading itself from disk until it
    /// exits, so replacing it underneath would crash it.
    /// </summary>
    public static void StartInstaller(string downloadedExe, IReadOnlyList<string> forwardArguments)
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Caminho do executável desconhecido.");
        var info = new ProcessStartInfo(downloadedExe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(downloadedExe)!,
        };
        info.ArgumentList.Add("--install-update");
        info.ArgumentList.Add(current);
        info.ArgumentList.Add("--wait-pid");
        info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var argument in forwardArguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process.Start(info);
        Log.Info("Atualização baixada. Fechando para instalar...");
    }

    /// <summary>
    /// Step 2 (the new .exe, running from the downloads folder): waits for the old app to close, copies itself over
    /// the old file and opens the updated app. Returns an error message, or null on success.
    /// </summary>
    public static string? RunInstaller(string targetExe, int oldProcessId, IReadOnlyList<string> forwardArguments)
    {
        try
        {
            using var old = Process.GetProcessById(oldProcessId);
            old.WaitForExit(60_000);
        }
        catch (ArgumentException)
        {
            // Already closed.
        }

        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Caminho do instalador desconhecido.");
        Exception? lastError = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                File.Copy(self, targetExe, overwrite: true);
                lastError = null;
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The old process, an antivirus scan or Explorer may hold the file for a moment.
                lastError = ex;
                Thread.Sleep(500);
            }
        }

        var info = new ProcessStartInfo(targetExe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(targetExe)!,
        };
        if (lastError is null)
        {
            info.ArgumentList.Add("--updated");
        }

        foreach (var argument in forwardArguments)
        {
            info.ArgumentList.Add(argument);
        }

        // Reopen the app either way: updated on success, the previous version if the copy failed.
        Process.Start(info);
        return lastError is null ? null : $"Não foi possível substituir {targetExe}: {lastError.Message}";
    }

    /// <summary>Removes the previous version (.old) and downloaded installers once the new version runs.</summary>
    public static async Task CleanupAfterUpdateAsync()
    {
        var current = Environment.ProcessPath;
        if (current is null)
        {
            return;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (File.Exists(current + ".old"))
                {
                    File.Delete(current + ".old");
                }

                if (Directory.Exists(UpdatesDirectory))
                {
                    Directory.Delete(UpdatesDirectory, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The old process may still be closing.
                await Task.Delay(1000);
            }
        }
    }

    private static async Task<string> ReadFeedAsync(CancellationToken cancellationToken)
    {
        var overridePath = Environment.GetEnvironmentVariable(FeedOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return await File.ReadAllTextAsync(overridePath, cancellationToken);
        }

        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return "{\"tag_name\":\"v0.0.0\",\"assets\":[]}";
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static async Task<Stream> OpenAsync(string url, CancellationToken cancellationToken)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return File.OpenRead(url);
        }

        var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    private static async Task<string> ReadChecksumAsync(string url, CancellationToken cancellationToken)
    {
        await using var stream = await OpenAsync(url, cancellationToken);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync(cancellationToken);
        // Accepts "HEX" or "HEX  PoPreyClip.exe" (sha256sum format).
        return text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PoPreyClip", CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
