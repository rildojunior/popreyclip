using System.Globalization;
using System.Text;
using PoPreyClip.Config;

namespace PoPreyClip.Voice;

/// <summary>How sure the Windows speech recognizer is about a phrase it heard.</summary>
public enum VoiceConfidence
{
    Rejected,
    Low,
    Medium,
    High,
}

/// <summary>Builds the phrases the recognizer listens for and decides which results save a clip.</summary>
public static class VoicePhrases
{
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Lower case, no punctuation, single spaces: "Oni, clipa ISSO!" → "oni clipa isso".</summary>
    public static string Normalize(string? text)
    {
        var builder = new StringBuilder();
        foreach (var c in (text ?? "").ToLower(Portuguese))
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }
        }

        return builder.ToString().TrimEnd();
    }

    public static int WordCount(string text) => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Every assistant name followed by every command: "oni clipa isso", "oni faz um clipe"...</summary>
    public static IReadOnlyList<string> Build(VoiceConfig config) =>
        config.AssistantNames.SelectMany(name => config.Commands.Select(command => Normalize($"{name} {command}")))
            .Where(p => p.Length > 0)
            .Distinct()
            .ToList();

    public static bool IsAccepted(VoiceConfidence confidence, string sensitivity) => sensitivity switch
    {
        VoiceConfig.SensitivityStrict => confidence == VoiceConfidence.High,
        VoiceConfig.SensitivityFlexible => confidence >= VoiceConfidence.Low,
        _ => confidence >= VoiceConfidence.Medium,
    };

    public static string Describe(VoiceConfidence confidence) => confidence switch
    {
        VoiceConfidence.High => "alta",
        VoiceConfidence.Medium => "média",
        VoiceConfidence.Low => "baixa",
        _ => "nenhuma",
    };
}
