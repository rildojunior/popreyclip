using System.Collections.Concurrent;
using System.IO.Pipes;
using PoPreyClip.Audio;

namespace PoPreyClip.Recording;

/// <summary>
/// Named pipe that ffmpeg reads as a raw PCM input. Writes are queued so the pacer never blocks;
/// audio produced before ffmpeg connects is discarded, and the wall-clock time of the first sample that
/// does go in is kept: it is time zero of the audio track, which the video is aligned to.
/// </summary>
internal sealed class PipeAudioSink : IAudioSink, IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    // 30 s of 10 ms chunks (~6 MB), so a short hiccup of the disk or ffmpeg never drops audio.
    private readonly BlockingCollection<byte[]> _queue = new(boundedCapacity: 3000);
    private readonly Thread _writer;
    private readonly TaskCompletionSource<DateTimeOffset> _firstSample = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _connected;
    private bool _warnedOverflow;

    public PipeAudioSink(string pipeName, string title)
    {
        PipeName = pipeName;
        Title = title;
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = $"Pipe {title}" };
        _writer.Start();
    }

    public string PipeName { get; }

    public string Title { get; }

    public string PipePath => $@"\\.\pipe\{PipeName}";

    /// <summary>Wall-clock time of the first sample ffmpeg received (the chunk just written was captured over the last few ms).</summary>
    public Task<DateTimeOffset> FirstSample => _firstSample.Task;

    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (!_connected || _queue.IsAddingCompleted)
        {
            return;
        }

        if (!_firstSample.Task.IsCompleted)
        {
            var chunk = TimeSpan.FromSeconds(pcm.Length / (double)(AudioFormat.SampleRate * AudioFormat.BytesPerFrame));
            _firstSample.TrySetResult(DateTimeOffset.UtcNow - chunk);
        }

        if (!_queue.TryAdd(pcm.ToArray()) && !_warnedOverflow)
        {
            _warnedOverflow = true;
            Log.Warn($"ffmpeg não está consumindo a faixa '{Title}' a tempo. Áudio descartado.");
        }
    }

    private void WriteLoop()
    {
        try
        {
            _pipe.WaitForConnection();
            _connected = true;
            foreach (var chunk in _queue.GetConsumingEnumerable())
            {
                _pipe.Write(chunk);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // ffmpeg closed the pipe (recording stopped), or the pipe was closed while a write waited on a stalled ffmpeg.
        }
        finally
        {
            _connected = false;
        }
    }

    public void Dispose()
    {
        _firstSample.TrySetCanceled();
        _queue.CompleteAdding();
        try
        {
            _pipe.Dispose();
        }
        catch (IOException)
        {
        }

        _writer.Join(1000);
        _queue.Dispose();
    }
}
