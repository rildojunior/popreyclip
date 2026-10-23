using System.Runtime.InteropServices;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace PoPreyClip.Voice;

public sealed record VoiceHeard(string Phrase, VoiceConfidence Confidence);

/// <summary>
/// Listens to the default Windows microphone for a closed list of phrases with the speech recognizer built into
/// Windows. It works offline with the Portuguese speech pack, costs well under 1% of a CPU core, and never touches
/// the game. Medal's default engine (the classic System.Speech one) has no Portuguese recognizer on most PCs.
/// </summary>
public sealed class VoiceCommandListener : IAsyncDisposable
{
    public const string LanguageTag = "pt-BR";
    private SpeechRecognizer? _recognizer;

    /// <summary>Raised on a background thread for every phrase heard, including rejected ones (empty phrase).</summary>
    public event Action<VoiceHeard>? Heard;

    /// <summary>Raised when Windows ends the listening by itself (microphone unplugged, audio problem...).</summary>
    public event Action<string>? StoppedUnexpectedly;

    /// <summary>Message to show when this PC can't listen in Portuguese, or null when it can.</summary>
    public static string? AvailabilityProblem()
    {
        try
        {
            return SpeechRecognizer.SupportedGrammarLanguages.Any(l => l.LanguageTag.Equals(LanguageTag, StringComparison.OrdinalIgnoreCase))
                ? null
                : "O Windows deste PC não tem o reconhecimento de fala em português. Instale em Configurações do Windows → Hora e idioma → Idioma e região → Português (Brasil) → Opções de idioma → Fala.";
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException or UnauthorizedAccessException)
        {
            return $"O reconhecimento de fala do Windows não está disponível: {ex.Message}";
        }
    }

    public async Task StartAsync(IReadOnlyList<string> phrases)
    {
        await StopAsync();
        var recognizer = new SpeechRecognizer(new Language(LanguageTag));
        try
        {
            recognizer.Constraints.Add(new SpeechRecognitionListConstraint(phrases, "clipe"));
            var compiled = await recognizer.CompileConstraintsAsync();
            if (compiled.Status != SpeechRecognitionResultStatus.Success)
            {
                throw new InvalidOperationException($"O Windows não aceitou as frases do comando de voz ({compiled.Status}).");
            }

            // Without this Windows stops listening after a few seconds of silence.
            recognizer.ContinuousRecognitionSession.AutoStopSilenceTimeout = TimeSpan.FromDays(1);
            recognizer.ContinuousRecognitionSession.ResultGenerated += OnResultGenerated;
            recognizer.ContinuousRecognitionSession.Completed += OnCompleted;
            await recognizer.ContinuousRecognitionSession.StartAsync();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            recognizer.Dispose();
            throw new InvalidOperationException(DescribeStartError(ex), ex);
        }

        _recognizer = recognizer;
    }

    public async Task StopAsync()
    {
        var recognizer = _recognizer;
        _recognizer = null;
        if (recognizer is null)
        {
            return;
        }

        recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResultGenerated;
        recognizer.ContinuousRecognitionSession.Completed -= OnCompleted;
        try
        {
            // Stopping waits for the phrase being spoken and sometimes never returns; cancelling is immediate.
            await recognizer.ContinuousRecognitionSession.CancelAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or TimeoutException or ObjectDisposedException)
        {
            Log.Warn($"Comando de voz: o Windows demorou para parar de escutar ({ex.Message}).");
        }

        recognizer.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void OnResultGenerated(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        var confidence = args.Result.Confidence switch
        {
            SpeechRecognitionConfidence.High => VoiceConfidence.High,
            SpeechRecognitionConfidence.Medium => VoiceConfidence.Medium,
            SpeechRecognitionConfidence.Low => VoiceConfidence.Low,
            _ => VoiceConfidence.Rejected,
        };
        Heard?.Invoke(new VoiceHeard(VoicePhrases.Normalize(args.Result.Text), confidence));
    }

    private void OnCompleted(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionCompletedEventArgs args)
    {
        if (_recognizer is not null && args.Status != SpeechRecognitionResultStatus.Success)
        {
            StoppedUnexpectedly?.Invoke(args.Status switch
            {
                SpeechRecognitionResultStatus.MicrophoneUnavailable => "o microfone padrão do Windows não está disponível",
                SpeechRecognitionResultStatus.AudioQualityFailure => "o áudio do microfone está com problema",
                SpeechRecognitionResultStatus.TimeoutExceeded => "o Windows parou de escutar por tempo",
                _ => $"o Windows parou de escutar ({args.Status})",
            });
        }
    }

    private static string DescribeStartError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "O Windows bloqueou o microfone. Libere em Configurações do Windows → Privacidade e segurança → Microfone → Permitir que aplicativos da área de trabalho acessem o microfone.",
        COMException { HResult: unchecked((int)0x80045509) } => "O Windows recusou o reconhecimento de fala por causa das configurações de privacidade de fala.",
        _ => $"Não foi possível ligar o comando de voz: {ex.Message}",
    };
}
