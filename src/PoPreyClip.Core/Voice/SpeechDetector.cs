namespace PoPreyClip.Voice;

/// <summary>
/// Cuts the microphone into utterances by loudness, so Whisper only runs when someone speaks. It follows the room's
/// background noise, keeps 0.3 s before the voice starts (the first syllable is often quiet) and closes the utterance
/// after 0.6 s of quiet. Utterances longer than a command are dropped: a long talk on Discord costs nothing.
/// </summary>
internal sealed class SpeechDetector(int sampleRate, int frameSamples, TimeSpan minimum, TimeSpan maximum)
{
    private const float MinimumLevel = 0.01f; // about -40 dBFS: quieter than this is never speech
    private const float SpeechFactor = 3.5f;
    private const int StartFrames = 2;
    private readonly int _prerollFrames = (int)(0.3 * sampleRate / frameSamples);
    private readonly int _endFrames = (int)(0.6 * sampleRate / frameSamples);
    private readonly int _minimumSamples = (int)(minimum.TotalSeconds * sampleRate);
    private readonly int _maximumSamples = (int)(maximum.TotalSeconds * sampleRate);
    private readonly Queue<float[]> _preroll = new();
    private readonly List<float> _utterance = [];
    private float _noise = MinimumLevel / 2;
    private int _loudFrames;
    private int _quietFrames;
    private bool _speaking;
    private bool _tooLong;

    /// <summary>Whisper needs at least a second of audio; shorter utterances are padded with silence.</summary>
    private int MinimumOutput => sampleRate + sampleRate / 5;

    /// <summary>Feeds one frame; returns a finished utterance of the right length, otherwise null.</summary>
    public float[]? Push(ReadOnlySpan<float> frame)
    {
        var level = Rms(frame);
        var loud = level > Math.Max(MinimumLevel, _noise * SpeechFactor);
        if (!_speaking)
        {
            if (!loud)
            {
                // The background noise is learned only while nobody speaks.
                _noise += (level - _noise) * 0.05f;
            }

            _loudFrames = loud ? _loudFrames + 1 : 0;
            _preroll.Enqueue(frame.ToArray());
            if (_preroll.Count > _prerollFrames)
            {
                _preroll.Dequeue();
            }

            if (_loudFrames >= StartFrames)
            {
                _speaking = true;
                _tooLong = false;
                _quietFrames = 0;
                _utterance.Clear();
                foreach (var previous in _preroll)
                {
                    _utterance.AddRange(previous);
                }

                _preroll.Clear();
            }

            return null;
        }

        _quietFrames = loud ? 0 : _quietFrames + 1;
        if (!_tooLong)
        {
            _utterance.AddRange(frame.ToArray());
            _tooLong = _utterance.Count > _maximumSamples;
        }

        if (_quietFrames < _endFrames)
        {
            return null;
        }

        _speaking = false;
        _loudFrames = 0;
        var spoken = _utterance.Count - _quietFrames * frameSamples;
        if (_tooLong || spoken < _minimumSamples)
        {
            _utterance.Clear();
            return null;
        }

        var result = new float[Math.Max(_utterance.Count, MinimumOutput)];
        _utterance.CopyTo(result);
        _utterance.Clear();
        return result;
    }

    private static float Rms(ReadOnlySpan<float> frame)
    {
        var sum = 0.0;
        foreach (var sample in frame)
        {
            sum += sample * sample;
        }

        return frame.Length == 0 ? 0 : (float)Math.Sqrt(sum / frame.Length);
    }
}
