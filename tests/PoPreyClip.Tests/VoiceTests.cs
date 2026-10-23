using PoPreyClip.Config;
using PoPreyClip.Voice;

namespace PoPreyClip.Tests;

public class VoiceTests
{
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

    [Theory]
    [InlineData(VoiceConfig.SensitivityStrict, VoiceConfidence.High, true)]
    [InlineData(VoiceConfig.SensitivityStrict, VoiceConfidence.Medium, false)]
    [InlineData(VoiceConfig.SensitivityNormal, VoiceConfidence.Medium, true)]
    [InlineData(VoiceConfig.SensitivityNormal, VoiceConfidence.Low, false)]
    [InlineData(VoiceConfig.SensitivityFlexible, VoiceConfidence.Low, true)]
    [InlineData(VoiceConfig.SensitivityFlexible, VoiceConfidence.Rejected, false)]
    public void Sensitivity_sets_the_lowest_confidence_that_saves_a_clip(string sensitivity, VoiceConfidence confidence, bool accepted) =>
        Assert.Equal(accepted, VoicePhrases.IsAccepted(confidence, sensitivity));
}
