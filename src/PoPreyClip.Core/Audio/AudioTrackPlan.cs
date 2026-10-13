using PoPreyClip.Config;

namespace PoPreyClip.Audio;

/// <summary>One output audio track and the source indexes mixed into it.</summary>
public sealed record AudioTrackSpec(string Title, IReadOnlyList<int> SourceIndexes);

public static class AudioTrackPlan
{
    public const string MixTitle = "Mix";

    /// <summary>
    /// Separate tracks: optional "Mix" track first, then one track per enabled source.
    /// Single track: every enabled source mixed into one track.
    /// </summary>
    public static List<AudioTrackSpec> Build(IReadOnlyList<AudioSourceConfig> enabledSources, AudioConfig audio)
    {
        var tracks = new List<AudioTrackSpec>();
        if (enabledSources.Count == 0)
        {
            return tracks;
        }

        var all = Enumerable.Range(0, enabledSources.Count).ToArray();
        if (!audio.MultipleAudioTracks)
        {
            tracks.Add(new AudioTrackSpec(MixTitle, all));
            return tracks;
        }

        if (audio.IncludeMixTrack && enabledSources.Count > 1)
        {
            tracks.Add(new AudioTrackSpec(MixTitle, all));
        }

        for (var i = 0; i < enabledSources.Count; i++)
        {
            tracks.Add(new AudioTrackSpec(enabledSources[i].Name, [i]));
        }

        return tracks;
    }
}
