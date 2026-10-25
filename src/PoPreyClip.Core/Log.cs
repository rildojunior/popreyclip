namespace PoPreyClip;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message);

/// <summary>Thread-safe logger that writes to a daily file, optionally to the console, and raises an event for the UI.</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoPreyClip", "logs");

    public static bool Verbose { get; set; }

    /// <summary>Console output is on for the command-line tool and off for the desktop app.</summary>
    public static bool WriteToConsole { get; set; } = true;

    /// <summary>Raised for every entry except debug ones (unless <see cref="Verbose"/> is on).</summary>
    public static event Action<LogEntry>? Logged;

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warning, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    private static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        var tag = level switch
        {
            LogLevel.Debug => "DBG ",
            LogLevel.Info => "INFO",
            LogLevel.Warning => "WARN",
            _ => "ERRO",
        };
        var line = $"[{entry.Time:HH:mm:ss.fff}] {tag} {message}";
        var visible = level != LogLevel.Debug || Verbose;
        lock (Gate)
        {
            if (visible && WriteToConsole)
            {
                Console.WriteLine(line);
            }

            try
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(Path.Combine(LogDirectory, $"popreyclip-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging must never break recording.
            }
        }

        if (visible)
        {
            Logged?.Invoke(entry);
        }
    }
}
