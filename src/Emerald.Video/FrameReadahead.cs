using System.Collections.Concurrent;

namespace Emerald.Video;

/// <summary>
/// Decodes ahead of a transmitter: a thread of its own pulling frames out of a
/// <see cref="FrameSource"/> into a small queue, so the transmitter takes a frame that is
/// already there instead of waiting for ffmpeg to produce one.
///
/// <b>Why it is needed.</b> A frame of 1080 video is four megabytes and the pipe from ffmpeg holds
/// a fraction of one, so ffmpeg can never get ahead of the reader: every frame was decoded,
/// converted and copied in the 40 ms the card allows for it, and any frame that took longer
/// left the card with nothing new - a stutter, or black at the very first frame of an item cut
/// into air. With this, the first second of an item is decoded before it is due, and a slow
/// frame later on is absorbed by the frames queued behind it.
///
/// <b>No allocation per frame.</b> The buffers are made once and passed round: the reader fills
/// a free one, the transmitter takes it, sends it, and hands it back.
/// </summary>
public sealed class FrameReadahead : IDisposable
{
    private readonly FrameSource _source;
    private readonly BlockingCollection<byte[]> _ready;
    private readonly BlockingCollection<byte[]> _free;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _reader;

    /// <summary>Frames decoded and waiting.</summary>
    public int Buffered => _ready.Count;

    /// <summary>True once the media has run out and every frame of it has been taken.</summary>
    public bool Finished => _ready.IsCompleted;

    /// <param name="depth">Frames to decode ahead. A second's worth rides out any ordinary stall.</param>
    public FrameReadahead(FrameSource source, int frameBytes, int depth)
    {
        _source = source;

        depth = Math.Max(2, depth);

        _ready = new BlockingCollection<byte[]>(depth);

        // Two more than the queue holds: the one the reader is filling, and the one the
        // transmitter is still holding on to as the frame on air.
        _free = new BlockingCollection<byte[]>(depth + 2);
        for (int i = 0; i < depth + 2; i++) _free.Add(new byte[frameBytes]);

        _reader = new Thread(Read) { IsBackground = true, Name = "cue decode ahead" };
        _reader.Start();
    }

    private void Read()
    {
        CancellationToken ct = _cts.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[] buffer = _free.Take(ct);

                if (!_source.TryReadFrame(buffer)) break;

                _ready.Add(buffer, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
        catch (System.IO.IOException) { }
        finally
        {
            try { _ready.CompleteAdding(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// The next frame, waiting up to <paramref name="wait"/> for it. False when there was none
    /// in time - check <see cref="Finished"/> to tell the end of the media from a slow decode.
    /// The frame must be handed back with <see cref="Return"/> once it has been sent.
    /// </summary>
    public bool TryTake(out byte[] frame, TimeSpan wait)
    {
        try
        {
            if (_ready.TryTake(out byte[]? taken, wait))
            {
                frame = taken;
                return true;
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }

        frame = Array.Empty<byte>();
        return false;
    }

    /// <summary>Gives a sent frame's buffer back for the reader to fill again.</summary>
    public void Return(byte[] frame)
    {
        if (frame.Length == 0) return;

        try { _free.TryAdd(frame); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Waits, briefly, until the queue holds <paramref name="frames"/> - or the media ends first.
    /// For a cue about to go to air, so its first frames are in hand when it does.
    /// </summary>
    public void WaitFor(int frames, TimeSpan atMost)
    {
        var until = DateTime.UtcNow + atMost;

        while (_ready.Count < frames && !_ready.IsAddingCompleted && DateTime.UtcNow < until)
            Thread.Sleep(5);
    }

    public void Dispose()
    {
        _cts.Cancel();

        // The reader may be inside a pipe read; ending the decoder is what lets it out.
        _source.Dispose();

        _reader.Join(TimeSpan.FromSeconds(2));

        _ready.Dispose();
        _free.Dispose();
        _cts.Dispose();
    }
}
