using Emerald.Core;

namespace Emerald.Video;

/// <summary>
/// Pictures out of a delay line, by their frame of the recording - for a viewer that has to show
/// the part of the recording no file can play yet.
///
/// The segment the recorder is writing has no index until it is closed, so nothing can open it.
/// But its last few minutes are already sitting in the delay line as raw frames, every one
/// stamped with its place in the recording - it is what tidal lock and Go Live send to air - so
/// a viewer can show those instead.
///
/// <b>It holds a claim on the line it reads.</b> A line is let go by the capture deck when the
/// recording stops, and a reader that did not hold it could be part-way through a frame when the
/// mapping went away. Whoever uses this calls <see cref="Use"/> with the line it found, and
/// <see cref="Dispose"/> at the end.
/// </summary>
public sealed class HeldPicture : IDisposable
{
    private DelayLine? _line;
    private DelayFrameBuffer? _frame;
    private byte[]? _bgra;

    /// <summary>The reduction per axis: half size, as the capture deck's own monitor shows it.</summary>
    public int Decimation { get; init; } = UyvyPreview.Decimation;

    /// <summary>The line being read, if one is held.</summary>
    public DelayLine? Line => _line;

    /// <summary>
    /// Reads from <paramref name="candidate"/> from now on - or keeps the line already held, if
    /// it is the same one. False when there is no line to read.
    /// </summary>
    public bool Use(IDelayLine? candidate)
    {
        if (ReferenceEquals(candidate, _line)) return _line is not null;

        Drop();

        if (candidate is not DelayLine line || line.IsSealed || !line.TryAddRef()) return false;

        _line = line;
        _frame = line.NewBuffer();
        return true;
    }

    /// <summary>
    /// The recording frames the line holds, oldest and newest, or null when it holds none.
    /// </summary>
    public (long Oldest, long Newest)? Reach()
    {
        if (_line is not { } line) return null;

        long written = line.FramesWritten;
        long newest = line.RecordingFrameAt(written - 1);

        // A slot or two in from the far end: the writer may be just about to take the oldest.
        long oldest = line.RecordingFrameAt(Math.Max(0, written - line.SlotCount + 2));

        return newest >= 0 && oldest >= 0 ? (oldest, newest) : null;
    }

    /// <summary>
    /// Frame <paramref name="recordingFrame"/> of the recording as BGRA, into a buffer this keeps
    /// and reuses - valid until the next call. False when the line does not hold it.
    /// </summary>
    public unsafe bool TryRead(long recordingFrame, out byte[] bgra, out int width, out int height)
    {
        bgra = Array.Empty<byte>();
        width = height = 0;

        if (_line is not { } line || _frame is not { } frame) return false;

        long seq = line.SequenceOf(recordingFrame);
        if (seq < 0 || line.TryRead(seq, frame) != DelayReadResult.Ok) return false;

        width = UyvyPreview.OutputWidth(line.Width, Decimation);
        height = UyvyPreview.OutputHeight(line.Height, Decimation);

        if (_bgra is null || _bgra.Length != width * height * 4)
            _bgra = UyvyPreview.CreateBuffer(line.Width, line.Height, Decimation);

        fixed (byte* source = frame.Video)
            UyvyPreview.ToBgra((IntPtr)source, line.Width, _bgra, width, height, Decimation);

        bgra = _bgra;
        return true;
    }

    private void Drop()
    {
        if (_line is not { } line) return;

        _line = null;
        _frame = null;

        // Off the caller's thread: the last release of a line closes and deletes its file.
        _ = Task.Run(() =>
        {
            try { line.Release(); } catch { /* the sweep will get the file */ }
        });
    }

    public void Dispose() => Drop();
}
