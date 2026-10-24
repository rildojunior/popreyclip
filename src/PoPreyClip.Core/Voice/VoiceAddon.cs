using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace PoPreyClip.Voice;

/// <summary>
/// Whisper (whisper.cpp, MIT) for the voice command. Its 2 MB of libraries travel inside the app and are written to
/// %LOCALAPPDATA%\PoPreyClip\voz\lib the first time they are needed; the speech model (Whisper tiny, 32 MB) is
/// downloaded only when the user turns the voice command on, so nobody else pays for it.
/// </summary>
public static class VoiceAddon
{
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny-q5_1.bin";
    private const string ModelSha256 = "818710568da3ca15689e31a743197b520007872ff9576237bda97bd1b469c3d7";
    private const long ModelSize = 32_152_673;

    /// <summary>Load order: each library needs the ones before it.</summary>
    private static readonly string[] Libraries = ["ggml-base-whisper.dll", "ggml-cpu-whisper.dll", "ggml-whisper.dll", "whisper.dll"];
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Busy = new(1, 1);
    private static readonly object LoadGate = new();
    private static IntPtr _whisper;
    private static double? _progress;

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoPreyClip", "voz");

    internal static string ModelPath => Path.Combine(Root, "ggml-tiny-q5_1.bin");

    private static string LibraryDirectory => Path.Combine(Root, "lib");

    /// <summary>Size of the download, in megabytes, shown on the settings page.</summary>
    public static int DownloadMegabytes => (int)Math.Round(ModelSize / 1048576.0);

    public static bool IsInstalled => File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ModelSize;

    /// <summary>Download progress from 0 to 1, or null when nothing is being downloaded.</summary>
    public static double? Progress => _progress;

    /// <summary>Raised when a download starts, advances (once per percent) or ends.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Downloads and checks the speech model when it is missing. Only one download runs at a time; a second call
    /// waits for it. Throws <see cref="HttpRequestException"/>, <see cref="IOException"/> or
    /// <see cref="InvalidDataException"/> on failure.
    /// </summary>
    public static async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        await Busy.WaitAsync(cancellationToken);
        try
        {
            if (IsInstalled)
            {
                return;
            }

            Log.Info($"Baixando o reconhecimento de voz ({DownloadMegabytes} MB)...");
            Directory.CreateDirectory(Root);
            SetProgress(0);
            var partial = ModelPath + ".download";
            try
            {
                await DownloadAsync(partial, cancellationToken);
                File.Move(partial, ModelPath, overwrite: true);
            }
            finally
            {
                TryDelete(partial);
            }

            Log.Info("Reconhecimento de voz instalado.");
        }
        finally
        {
            SetProgress(null);
            Busy.Release();
        }
    }

    /// <summary>
    /// Opens the speech model. The first call writes the libraries next to the model and teaches Whisper.net to load
    /// them from there (its own search only looks beside the .exe, which a single-file app does not have).
    /// </summary>
    internal static WhisperFactory OpenModel()
    {
        lock (LoadGate)
        {
            if (_whisper == IntPtr.Zero)
            {
                WriteLibraries();
                foreach (var library in Libraries)
                {
                    _whisper = NativeLibrary.Load(Path.Combine(LibraryDirectory, library));
                }

                NativeLibrary.SetDllImportResolver(typeof(WhisperFactory).Assembly,
                    (name, _, _) => name == "libwhisper" ? _whisper : IntPtr.Zero);
                RuntimeOptions.LoadedLibrary = RuntimeLibrary.Cpu;
            }
        }

        return WhisperFactory.FromPath(ModelPath);
    }

    /// <summary>Copies the embedded libraries out of the app, unless the same files are already there.</summary>
    private static void WriteLibraries()
    {
        Directory.CreateDirectory(LibraryDirectory);
        var assembly = typeof(VoiceAddon).Assembly;
        foreach (var library in Libraries)
        {
            using var source = assembly.GetManifestResourceStream("whisper\\" + library)
                ?? throw new InvalidOperationException($"O app não tem a biblioteca {library}.");
            var target = Path.Combine(LibraryDirectory, library);
            if (File.Exists(target) && new FileInfo(target).Length == source.Length)
            {
                continue;
            }

            using var output = File.Create(target);
            source.CopyTo(output);
        }
    }

    private static async Task DownloadAsync(string target, CancellationToken cancellationToken)
    {
        using (var response = await Http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(target);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                SetProgress(Math.Min(1, done / (double)ModelSize));
            }
        }

        await using var downloaded = File.OpenRead(target);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, cancellationToken));
        if (!actual.Equals(ModelSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("O arquivo do reconhecimento de voz veio diferente do esperado (SHA-256). Tente de novo.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Notifies at most once per percent, so the settings page is not flooded during the download.</summary>
    private static void SetProgress(double? value)
    {
        var changed = value is null || _progress is null || (int)(value * 100) != (int)(_progress * 100);
        _progress = value;
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PoPreyClip", "1.0"));
        return client;
    }
}
