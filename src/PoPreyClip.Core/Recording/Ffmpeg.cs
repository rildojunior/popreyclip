using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PoPreyClip.Recording;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs short-lived ffmpeg or ffprobe commands with argument lists (no shell quoting).</summary>
internal static class Ffmpeg
{
    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Não foi possível iniciar {executable}");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    public static async Task<ProcessResult> RunCheckedAsync(string executable, IEnumerable<string> arguments, string what)
    {
        var result = await RunAsync(executable, arguments);
        if (result.ExitCode != 0)
        {
            var tail = string.Join(Environment.NewLine, result.StandardError.Split('\n').TakeLast(8));
            throw new InvalidOperationException($"{what} falhou (código {result.ExitCode}):{Environment.NewLine}{tail}");
        }

        return result;
    }

    public static async Task<double> ProbeDurationAsync(string ffprobe, string file)
    {
        var result = await RunCheckedAsync(ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file], "ffprobe");
        return double.TryParse(result.StandardOutput.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
    }

    public static bool IsAvailable(string executable)
    {
        try
        {
            var result = RunAsync(executable, ["-hide_banner", "-version"]).GetAwaiter().GetResult();
            return result.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
