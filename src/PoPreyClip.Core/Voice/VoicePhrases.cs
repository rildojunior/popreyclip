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

    /// <summary>
    /// Finds a phrase in what Whisper wrote: the assistant's name, then the command up to two words later
    /// ("oni faz um clipa isso" still holds "oni ... clipa isso"). Each word may differ by a letter (two in long
    /// words), because Whisper writes "Ôni" or "clipe isso" now and then; the confidence says how close it was.
    /// The command without the name never counts.
    /// </summary>
    public static VoiceHeard Match(string transcript, VoiceConfig config)
    {
        var heard = Fold(transcript).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var best = new VoiceHeard("", VoiceConfidence.Rejected);
        foreach (var name in config.AssistantNames)
        {
            var nameWords = Fold(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var command in config.Commands)
            {
                var commandWords = Fold(command).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < heard.Length; i++)
                {
                    if (WordsAt(heard, i, nameWords) is not { } nameDistance)
                    {
                        continue;
                    }

                    for (var gap = 0; gap <= 2; gap++)
                    {
                        if (WordsAt(heard, i + nameWords.Length + gap, commandWords) is { } commandDistance)
                        {
                            var confidence = Math.Max(nameDistance, commandDistance) switch
                            {
                                0 => VoiceConfidence.High,
                                1 => VoiceConfidence.Medium,
                                _ => VoiceConfidence.Low,
                            };
                            if (confidence > best.Confidence)
                            {
                                best = new VoiceHeard($"{name} {command}", confidence);
                            }
                        }
                    }
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Second opinion on a phrase found with the hint: what Whisper wrote without the hint must still sound like it.
    /// With the phrases as a hint, Whisper sometimes writes one of them for unclear speech ("quatrocentos por..."
    /// became "oni cria um clipe"); without it, it writes what it heard ("one clipe a isso" for "oni clipa isso").
    /// They are compared by sound, without spaces ("Quico, clipe a isso" sounds like "kiko clipa isso"), and a third
    /// of the sounds may differ.
    /// </summary>
    public static bool Confirms(string transcript, string phrase)
    {
        var heard = Sound(transcript);
        var expected = Sound(phrase);
        if (expected.Length == 0)
        {
            return false;
        }

        // Edit distance from the phrase to the closest stretch of the transcript (starting and ending anywhere).
        var previous = Enumerable.Range(0, expected.Length + 1).ToArray();
        var best = previous[expected.Length];
        foreach (var c in heard)
        {
            var current = new int[expected.Length + 1];
            for (var j = 1; j <= expected.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (c == expected[j - 1] ? 0 : 1));
            }

            best = Math.Min(best, current[expected.Length]);
            previous = current;
        }

        return best <= expected.Length / 3;
    }

    /// <summary>
    /// Letters as they sound in Portuguese, without spaces: "Quico, CLIPE a isso" → "kikoklipeaiso". Different
    /// spellings of the same sound become the same letter, so made-up names compare well.
    /// </summary>
    internal static string Sound(string text)
    {
        var letters = Fold(text).Replace(" ", "");
        var builder = new StringBuilder();
        for (var i = 0; i < letters.Length; i++)
        {
            var c = letters[i];
            var next = i + 1 < letters.Length ? letters[i + 1] : '\0';
            var sound = c switch
            {
                'q' when next == 'u' && i + 2 < letters.Length && letters[i + 2] is 'e' or 'i' => SkipNext(ref i, 'k'),
                'q' => 'k',
                'c' when next == 'h' => SkipNext(ref i, 'x'),
                'c' when next is 'e' or 'i' => 's',
                'c' => 'k',
                'l' or 'n' when next == 'h' => SkipNext(ref i, c),
                'p' when next == 'h' => SkipNext(ref i, 'f'),
                'h' => '\0',
                'y' => 'i',
                'w' => 'v',
                'z' => 's',
                _ => c,
            };
            if (sound != '\0' && (builder.Length == 0 || builder[^1] != sound))
            {
                builder.Append(sound);
            }
        }

        return builder.ToString();
    }

    private static char SkipNext(ref int i, char sound)
    {
        i++;
        return sound;
    }

    /// <summary>Largest letter difference when <paramref name="words"/> sit at <paramref name="start"/>, or null if they don't.</summary>
    private static int? WordsAt(string[] heard, int start, string[] words)
    {
        if (words.Length == 0 || start + words.Length > heard.Length)
        {
            return null;
        }

        var worst = 0;
        for (var k = 0; k < words.Length; k++)
        {
            var allowed = words[k].Length <= 2 ? 0 : words[k].Length <= 5 ? 1 : 2;
            var distance = Distance(heard[start + k], words[k]);
            if (distance > allowed)
            {
                return null;
            }

            worst = Math.Max(worst, distance);
        }

        return worst;
    }

    /// <summary>Lower case without accents or punctuation: "Ôni, CLIPA isso!" → "oni clipa isso".</summary>
    internal static string Fold(string text)
    {
        var decomposed = Normalize(text).Normalize(NormalizationForm.FormD);
        return new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }

    /// <summary>Letters to add, remove or change to turn one word into the other.</summary>
    internal static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            previous = current;
        }

        return previous[b.Length];
    }

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
