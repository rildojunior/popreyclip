using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoPreyClip.Config;

/// <summary>
/// Owns the current configuration. Readers get an immutable snapshot through <see cref="Current"/>;
/// changes go through <see cref="Update"/>, which swaps in a modified copy (so the audio thread never
/// sees a half-edited object), raises <see cref="Changed"/> and saves the file shortly after.
/// Edits made to the file by hand are picked up as well.
/// </summary>
public sealed class ConfigStore : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;
    private Timer? _saveTimer;
    private DateTime _ignoreFileEventsUntil;
    private volatile AppConfig _current;

    private ConfigStore(string path, AppConfig config)
    {
        FilePath = path;
        _current = config;
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PoPreyClip", "config.json");

    public string FilePath { get; }

    /// <summary>Snapshot of the configuration. Do not modify it; use <see cref="Update"/>.</summary>
    public AppConfig Current => _current;

    /// <summary>Raised after every change, from the UI or from the file. May run on any thread.</summary>
    public event Action<AppConfig>? Changed;

    public static ConfigStore LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            var defaults = new AppConfig();
            defaults.Normalize();
            Save(path, defaults);
            return new ConfigStore(path, defaults);
        }

        return new ConfigStore(path, Read(path));
    }

    public static AppConfig Read(string path)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"Configuração vazia: {path}");
        config.Normalize();
        return config;
    }

    public static void Save(string path, AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public static AppConfig Clone(AppConfig config) =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config, JsonOptions), JsonOptions)!;

    /// <summary>Applies a change immediately in memory and saves it to disk shortly after.</summary>
    public void Update(Action<AppConfig> change)
    {
        AppConfig next;
        lock (_gate)
        {
            next = Clone(_current);
            change(next);
            next.Normalize();
            _current = next;
            _saveTimer ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }

        Changed?.Invoke(next);
    }

    /// <summary>Writes pending changes right away (used when the app closes).</summary>
    public void Flush()
    {
        _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        SaveNow();
    }

    private void SaveNow()
    {
        lock (_gate)
        {
            try
            {
                _ignoreFileEventsUntil = DateTime.UtcNow.AddSeconds(1);
                Save(FilePath, _current);
            }
            catch (IOException ex)
            {
                Log.Warn($"Não foi possível salvar a configuração: {ex.Message}");
            }
        }
    }

    public void StartWatching()
    {
        _reloadTimer = new Timer(_ => ReloadFromFile(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(Path.GetFullPath(FilePath))!, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        FileSystemEventHandler onChange = (_, _) => ScheduleReload();
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Renamed += (_, _) => ScheduleReload();
        _watcher.EnableRaisingEvents = true;
    }

    private void ScheduleReload()
    {
        if (DateTime.UtcNow >= _ignoreFileEventsUntil)
        {
            _reloadTimer?.Change(300, Timeout.Infinite);
        }
    }

    private void ReloadFromFile()
    {
        try
        {
            var next = Read(FilePath);
            _current = next;
            Log.Info("Configuração recarregada do arquivo.");
            Changed?.Invoke(next);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            Log.Warn($"Não foi possível recarregar a configuração: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadTimer?.Dispose();
        if (_saveTimer is not null)
        {
            Flush();
            _saveTimer.Dispose();
        }
    }
}
