using System.Runtime.InteropServices;
using PoPreyClip.Config;
using PoPreyClip.Voice;

namespace PoPreyClip.Tests;

public class VoiceTests
{
    private static VoiceConfig Defaults()
    {
        var voice = new VoiceConfig();
        voice.Normalize();
        return voice;
    }

    [Fact]
    public void Default_phrases_put_the_assistant_name_before_each_command()
    {
        var config = new AppConfig();
        config.Normalize();

        Assert.False(config.Voice.Enabled);
        Assert.Equal(["oni clipa isso", "oni faz um clipe"], VoicePhrases.Build(config.Voice));
    }

    [Fact]
    public void Phrases_combine_every_name_with_every_command()
    {
        var voice = new VoiceConfig { AssistantNames = ["Oni", "Jarvis"], Commands = ["clipa isso", "salva"] };
        voice.Normalize();

        Assert.Equal(["oni clipa isso", "oni salva", "jarvis clipa isso", "jarvis salva"], VoicePhrases.Build(voice));
    }

    [Theory]
    [InlineData("  Oni,  CLIPA isso! ", "oni clipa isso")]
    [InlineData("Ônibus", "ônibus")]
    [InlineData("...", "")]
    public void Normalize_lowercases_and_drops_punctuation(string text, string expected) =>
        Assert.Equal(expected, VoicePhrases.Normalize(text));

    [Fact]
    public void Normalize_config_removes_blanks_and_duplicates_and_keeps_defaults_when_empty()
    {
        var voice = new VoiceConfig { AssistantNames = ["", "  "], Commands = ["Clipa isso", "clipa  isso", ""], Sensitivity = "?" };
        voice.Normalize();

        Assert.Equal(["oni"], voice.AssistantNames);
        Assert.Equal(["clipa isso"], voice.Commands);
        Assert.Equal(VoiceConfig.SensitivityNormal, voice.Sensitivity);
    }

    // What Whisper wrote in the tests with the Windows voice, and a few made-up near misses.
    [Theory]
    [InlineData("Oni, clipa isso.", "oni clipa isso", VoiceConfidence.High)]
    [InlineData("Ôni, clipa isso!", "oni clipa isso", VoiceConfidence.High)]
    [InlineData("Oni, faz um clipa isso.", "oni clipa isso", VoiceConfidence.High)]
    [InlineData("ONI, faz um clipe.", "oni faz um clipe", VoiceConfidence.High)]
    [InlineData("Honi, clipe isso.", "oni clipa isso", VoiceConfidence.Medium)]
    [InlineData("Clipa isso.", "", VoiceConfidence.Rejected)]
    [InlineData("Uníso do Oni, bus.", "", VoiceConfidence.Rejected)]
    [InlineData("Vamos lá pessoal, cuidado com o tanque à esquerda.", "", VoiceConfidence.Rejected)]
    [InlineData("Ou é na, clipa isso.", "", VoiceConfidence.Rejected)]
    public void A_phrase_is_the_name_then_the_command_with_small_spelling_differences(string transcript, string phrase, VoiceConfidence confidence)
    {
        var heard = VoicePhrases.Match(transcript, Defaults());

        Assert.Equal(phrase, heard.Phrase);
        Assert.Equal(confidence, heard.Confidence);
    }

    // What Whisper wrote without the hint, on audio from real clips.
    [Theory]
    [InlineData("one clipe a isso.", "oni clipa isso", true)]
    [InlineData("Ô né, faz um clipe.", "oni faz um clipe", true)]
    [InlineData("Quico, flipper isso.", "kiko clipa isso", true)]
    [InlineData("Járez, salva isso.", "jarvis salva isso", true)]
    [InlineData("novo, grava aí", "lobo grava aí", true)]
    [InlineData("4 centos por preie", "oni cria um clipe", false)]
    [InlineData("Por que ele clipos em que ele...", "oni clipa isso", false)]
    [InlineData("", "oni clipa isso", false)]
    public void A_phrase_found_with_the_hint_must_still_sound_alike_without_it(string transcript, string phrase, bool confirmed) =>
        Assert.Equal(confirmed, VoicePhrases.Confirms(transcript, phrase));

    [Theory]
    [InlineData("Quico, CLIPE a isso!", "kikoklipeaiso")]
    [InlineData("Chuva, quero ilha", "xuvakeroila")]
    [InlineData("Wesley pizza", "vesleipisa")]
    public void Sound_spells_alike_what_sounds_alike(string text, string expected) =>
        Assert.Equal(expected, VoicePhrases.Sound(text));

    [Theory]
    [InlineData(VoiceConfig.SensitivityStrict, VoiceConfidence.High, true)]
    [InlineData(VoiceConfig.SensitivityStrict, VoiceConfidence.Medium, false)]
    [InlineData(VoiceConfig.SensitivityNormal, VoiceConfidence.Medium, true)]
    [InlineData(VoiceConfig.SensitivityNormal, VoiceConfidence.Low, false)]
    [InlineData(VoiceConfig.SensitivityFlexible, VoiceConfidence.Low, true)]
    [InlineData(VoiceConfig.SensitivityFlexible, VoiceConfidence.Rejected, false)]
    public void Sensitivity_sets_the_lowest_confidence_that_saves_a_clip(string sensitivity, VoiceConfidence confidence, bool accepted) =>
        Assert.Equal(accepted, VoicePhrases.IsAccepted(confidence, sensitivity));

    [Fact]
    public void Microphone_audio_becomes_16_kHz_mono_for_whisper()
    {
        // Three 48 kHz stereo frames (left, right) become one 16 kHz sample: their average.
        short[] stereo = [100, 300, 200, 400, 300, 500, -16384, -16384, -16384, -16384, -16384, -16384];
        var output = new float[2];

        VoiceCommandListener.ToMono16k(MemoryMarshal.AsBytes(stereo.AsSpan()), output);

        Assert.Equal(300 / 32768f, output[0], 6);
        Assert.Equal(-0.5f, output[1], 6);
    }

    [Fact]
    public void Speech_detector_returns_short_utterances_and_ignores_silence_and_long_talk()
    {
        const int rate = 16_000, frame = 320;
        var detector = new SpeechDetector(rate, frame, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(4));
        var quiet = new float[frame];
        var voice = Enumerable.Range(0, frame).Select(i => (float)(0.2 * Math.Sin(i * 0.3))).ToArray();

        float[]? Feed(float[] samples, double seconds)
        {
            float[]? result = null;
            for (var i = 0; i < seconds * rate / frame; i++)
            {
                result ??= detector.Push(samples);
            }

            return result;
        }

        Assert.Null(Feed(quiet, 2));
        Assert.Null(Feed(voice, 1.5));
        var utterance = Feed(quiet, 1);
        Assert.NotNull(utterance);
        Assert.InRange(utterance.Length, (int)(1.5 * rate), (int)(2.6 * rate));

        // A 6-second talk is longer than any command: dropped.
        Assert.Null(Feed(voice, 6));
        Assert.Null(Feed(quiet, 1));
    }
}
