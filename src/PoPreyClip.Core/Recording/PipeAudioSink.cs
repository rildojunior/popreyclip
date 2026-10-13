using System.Collections.Concurrent;
using System.IO.Pipes;
using PoPreyClip.Audio;

namespace PoPreyClip.Recording;

/// <summary>
/// Named pipe that ffmpeg reads as a raw PCM input. Writes are queued so the pacer never blocks;
/// audio produced before ffmpeg connects is discarded so the track starts in sync.
/// </summary>
internal sealed class PipeAudioSink : IAudioSink, IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly BlockingCollection<byte[]> _queue = new(boundedCapacity: 500);
    private readonly Thread _writer;
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

    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (!_connected || _queue.IsAddingCompleted)
        {
            return;
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
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // ffmpeg closed the pipe (recording stopped).
        }
        finally
        {
            _connected = false;
        }
    }

    public void Dispose()
    {
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
