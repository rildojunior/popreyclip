using PoPreyClip.Config;
using PoPreyClip.Session;

namespace PoPreyClip.Voice;

/// <summary>
/// Saves a clip when the user says one of the voice phrases. Listens only while the option is on and clip capture is
/// active, so the microphone is not used for this in idle mode.
/// </summary>
public sealed class VoiceClipTrigger : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    private readonly ConfigStore _store;
    private readonly RecordingSession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VoiceCommandListener? _listener;

    /// <summary>Phrases being listened for (or that failed to start), joined; null while not listening.</summary>
    private string? _signature;
    private bool _paused;
    private bool _disposed;
    private string? _problem;

    public VoiceClipTrigger(ConfigStore store, RecordingSession session)
    {
        _store = store;
        _session = session;
        _session.RecordingChanged += OnRecordingChanged;
        _store.Changed += OnConfigChanged;
        VoiceAddon.Changed += OnAddonChanged;
        Refresh();
    }

    /// <summary>Why the voice command can't listen right now, or null.</summary>
    public string? Problem
    {
        get => _problem;
        private set
        {
            if (_problem != value)
            {
                _problem = value;
                ProblemChanged?.Invoke(value);
            }
        }
    }

    public event Action<string?>? ProblemChanged;

    /// <summary>Stops listening while the settings page tests the phrases, so a test never saves a clip.</summary>
    public async Task SetPausedAsync(bool paused)
    {
        _paused = paused;
        await RefreshAsync();
    }

    private void OnRecordingChanged(bool recording) => Refresh();

    private void OnConfigChanged(AppConfig config) => Refresh();

    /// <summary>The download finished: start listening if the voice command is waiting for it.</summary>
    private void OnAddonChanged()
    {
        if (VoiceAddon.Progress is null)
        {
            Refresh();
        }
    }

    private void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var config = _store.Current.Voice;
            var phrases = VoicePhrases.Build(config);
            var microphone = Microphone(_store.Current);
            var wanted = !_disposed && !_paused && config.Enabled && _session.IsRecording && phrases.Count > 0;
            // Listening restarts when the phrases or the chosen microphone change, or once the download is done.
            var signature = wanted ? $"{string.Join("|", phrases)}|{microphone}|{VoiceAddon.IsInstalled}" : null;
            if (signature == _signature)
            {
                return;
            }

            await StopListenerAsync();
            _signature = signature;
            if (signature is null)
            {
                Problem = null;
                return;
            }

            if (!VoiceAddon.IsInstalled)
            {
                // Normally downloaded when the option is turned on; this covers a deleted or interrupted download.
                Problem = null;
                _ = InstallAddonAsync();
                return;
            }

            var listener = new VoiceCommandListener();
            listener.Heard += OnHeard;
            listener.StoppedUnexpectedly += OnStoppedUnexpectedly;
            try
            {
                await listener.StartAsync(config, microphone);
                _listener = listener;
                Problem = null;
                Log.Info($"Comando de voz ligado. Diga \"{phrases[0]}\" para salvar um clipe.");
            }
            catch (InvalidOperationException ex)
            {
                Problem = ex.Message;
                Log.Warn($"Comando de voz: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The microphone set on the Audio page ("Auto" for the Windows default).</summary>
    private static string? Microphone(AppConfig config) =>
        config.Audio.Sources.FirstOrDefault(s => s.Id == AudioSourceIds.Microphone)?.Device;

    private async Task InstallAddonAsync()
    {
        try
        {
            await VoiceAddon.InstallAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Problem = $"Não foi possível baixar o reconhecimento de voz: {ex.Message}";
            Log.Warn($"Comando de voz: {Problem}");
        }
    }

    private async Task StopListenerAsync()
    {
        var listener = _listener;
        _listener = null;
        if (listener is null)
        {
            return;
        }

        listener.Heard -= OnHeard;
        listener.StoppedUnexpectedly -= OnStoppedUnexpectedly;
        await listener.StopAsync();
        Log.Info("Comando de voz desligado.");
    }

    private void OnHeard(VoiceHeard heard)
    {
        if (heard.Phrase.Length == 0)
        {
            return;
        }

        var confidence = VoicePhrases.Describe(heard.Confidence);
        if (!VoicePhrases.IsAccepted(heard.Confidence, _store.Current.Voice.Sensitivity))
        {
            Log.Info($"Comando de voz ignorado: \"{heard.Phrase}\" com confiança {confidence}.");
            return;
        }

        Log.Info($"Comando de voz: \"{heard.Phrase}\" (confiança {confidence}).");
        _ = _session.SaveClipAsync();
    }

    /// <summary>Windows stopped listening (microphone unplugged...): try again a little later.</summary>
    private void OnStoppedUnexpectedly(string reason)
    {
        Log.Warn($"Comando de voz parou: {reason}. Tentando de novo em {RetryDelay.TotalSeconds:0} s.");
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                await StopListenerAsync();
                _signature = null;
            }
            finally
            {
                _gate.Release();
            }

            await Task.Delay(RetryDelay);
            await RefreshAsync();
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _session.RecordingChanged -= OnRecordingChanged;
        _store.Changed -= OnConfigChanged;
        VoiceAddon.Changed -= OnAddonChanged;
        // Off the UI thread: waiting there for code that continues on the UI thread would never finish.
        Task.Run(RefreshAsync).Wait(TimeSpan.FromSeconds(5));
    }
}
