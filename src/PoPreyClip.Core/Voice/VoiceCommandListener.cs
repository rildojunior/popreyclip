using System.Runtime.InteropServices;
using PoPreyClip.Audio;
using PoPreyClip.Config;
using Whisper.net;

namespace PoPreyClip.Voice;

public sealed record VoiceHeard(string Phrase, VoiceConfidence Confidence);

/// <summary>
/// Listens for the voice phrases with Whisper (tiny model), offline. It reads the chosen microphone itself, so it keeps
/// hearing while the game is in front (the Windows speech recognizer goes deaf as soon as PoPreyClip is in the
/// background), and Whisper writes any word, so names and slang like "clipa isso" work. To stay light, Whisper only
/// runs on short utterances: silence and long conversations cost almost nothing.
/// </summary>
public sealed class VoiceCommandListener : IAsyncDisposable
{
    internal const int SampleRate = 16_000;
    private const int Decimation = AudioFormat.SampleRate / SampleRate;
    private const int FrameSamples = SampleRate / 50; // 20 ms
    private static readonly TimeSpan MaxUtterance = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MinUtterance = TimeSpan.FromMilliseconds(400);
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>Raised on a background thread after each short utterance, with the phrase found or a rejected empty one.</summary>
    public event Action<VoiceHeard>? Heard;

    /// <summary>Raised with the raw text Whisper wrote, for the log and the settings test.</summary>
    public event Action<string>? Transcribed;

    /// <summary>Raised when listening stops by itself (microphone unplugged or failing).</summary>
    public event Action<string>? StoppedUnexpectedly;

    /// <summary>Starts listening on the microphone ("Auto" or part of its name, as on the Audio page).</summary>
    public async Task StartAsync(VoiceConfig config, string? microphone)
    {
        await StopAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _running = true;
        // A thread of its own: the capture lives in the multithreaded COM apartment and Whisper runs after each utterance.
        _thread = new Thread(() => Listen(config, microphone, started)) { IsBackground = true, Name = "Comando de voz" };
        _thread.Start();
        await started.Task;
    }

    public Task StopAsync()
    {
        var thread = _thread;
        _thread = null;
        _running = false;
        return thread is null ? Task.CompletedTask : Task.Run(() => thread.Join(TimeSpan.FromSeconds(5)));
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void Listen(VoiceConfig config, string? microphone, TaskCompletionSource started)
    {
        WasapiCapture? capture = null;
        WhisperFactory? factory = null;
        WhisperProcessor? processor = null;
        WhisperProcessor? plain = null;
        try
        {
            var device = AudioDevices.ResolveMicrophone(microphone)
                ?? throw new InvalidOperationException("Nenhum microfone encontrado.");
            factory = VoiceAddon.OpenModel();
            // The phrases as a hint make Whisper prefer their spelling ("clipa", the assistant's name); a phrase it
            // finds is then checked again without the hint (see VoicePhrases.Confirms).
            processor = CreateProcessor(factory, string.Join(". ", VoicePhrases.Build(config)) + ".");
            plain = CreateProcessor(factory, null);

            var fifo = new PcmFifo(AudioFormat.SampleRate * AudioFormat.BytesPerFrame * 2);
            capture = AudioDevices.CreateMicrophoneCapture(device.Id, fifo, "Comando de voz");
            capture.Start();
            started.TrySetResult();
            Log.Info($"Comando de voz escutando: {device.Name}.");
            ReadLoop(capture, fifo, processor, plain, config);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            var message = ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException
                ? $"O reconhecimento de voz não abriu neste PC ({ex.Message})."
                : $"Não foi possível ligar o comando de voz: {ex.Message}";
            if (!started.TrySetException(new InvalidOperationException(message, ex)))
            {
                StoppedUnexpectedly?.Invoke(message);
            }
        }
        finally
        {
            capture?.Dispose();
            processor?.Dispose();
            plain?.Dispose();
            factory?.Dispose();
        }
    }

    private static WhisperProcessor CreateProcessor(WhisperFactory factory, string? prompt)
    {
        var builder = factory.CreateBuilder()
            .WithLanguage("pt")
            .WithThreads(2)
            .WithNoContext()
            .WithSingleSegment()
            // A 5 s window instead of 30 s and no retries: about 0.1 s per utterance, and no runaway repetitions.
            .WithAudioContextSize(256)
            .WithMaxTokensPerSegment(16)
            .WithTemperature(0)
            .WithTemperatureInc(0);
        return (prompt is null ? builder : builder.WithPrompt(prompt)).Build();
    }

    private void ReadLoop(WasapiCapture capture, PcmFifo fifo, WhisperProcessor processor, WhisperProcessor plain, VoiceConfig config)
    {
        var chunk = new byte[FrameSamples * Decimation * AudioFormat.BytesPerFrame];
        var frame = new float[FrameSamples];
        var detector = new SpeechDetector(SampleRate, FrameSamples, MinUtterance, MaxUtterance);
        while (_running)
        {
            if (capture.Faulted)
            {
                StoppedUnexpectedly?.Invoke("o microfone parou de responder");
                return;
            }

            if (fifo.Available < chunk.Length)
            {
                Thread.Sleep(10);
                continue;
            }

            fifo.Read(chunk);
            ToMono16k(chunk, frame);
            if (detector.Push(frame) is { } utterance)
            {
                var text = Transcribe(processor, utterance);
                var heard = VoicePhrases.Match(text, config);
                if (heard.Phrase.Length > 0)
                {
                    // Only here, so it costs a second pass just when a phrase seems to be there.
                    var check = Transcribe(plain, utterance);
                    if (!VoicePhrases.Confirms(check, heard.Phrase))
                    {
                        text = check; // what was really said, for the settings test
                        heard = new VoiceHeard("", VoiceConfidence.Rejected);
                    }
                }

                Transcribed?.Invoke(text);
                Heard?.Invoke(heard);
            }
        }
    }

    private static string Transcribe(WhisperProcessor processor, float[] samples)
    {
        var text = "";
        foreach (var segment in processor.ProcessAsync(samples).ToBlockingEnumerable())
        {
            text += segment.Text;
        }

        return text.Trim();
    }

    /// <summary>48 kHz stereo 16-bit to 16 kHz mono float: average both channels over each group of three frames.</summary>
    internal static void ToMono16k(ReadOnlySpan<byte> stereo48k, Span<float> mono16k)
    {
        var samples = MemoryMarshal.Cast<byte, short>(stereo48k);
        var count = Math.Min(samples.Length / AudioFormat.Channels / Decimation, mono16k.Length);
        for (var i = 0; i < count; i++)
        {
            var sum = 0;
            for (var f = 0; f < Decimation; f++)
            {
                var at = (i * Decimation + f) * AudioFormat.Channels;
                sum += samples[at] + samples[at + 1];
            }

            mono16k[i] = sum / (Decimation * AudioFormat.Channels * 32768f);
        }
    }
}
