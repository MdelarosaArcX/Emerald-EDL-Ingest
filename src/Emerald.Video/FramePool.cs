using System.Collections.Concurrent;

namespace Emerald.Video;

/// <summary>
/// One frame's buffer, and a count of who still needs it.
///
/// <b>The rule.</b> Whoever is handed a lease may read <see cref="Buffer"/> until they return. To
/// keep it past that - on a queue, for another thread - they call <see cref="Retain"/> before
/// handing it on and <see cref="Release"/> exactly once when that reader is done with it. Nobody
/// writes to the buffer after it is filled.
///
/// A release too many is the one thing that must not happen: the buffer would go back to the pool
/// while a ring's writer is still copying it, and the next frame off the receiver would be written
/// over the one going to air. So a count that goes below nothing is refused rather than acted on.
/// </summary>
public sealed class FrameLease
{
    private readonly FramePool? _pool;
    private int _holders = 1;

    internal FrameLease(byte[] buffer, FramePool? pool)
    {
        Buffer = buffer;
        _pool = pool;
    }

    /// <summary>The frame. Exactly the frame's length: readers may rely on <c>Buffer.Length</c>.</summary>
    public byte[] Buffer { get; }

    /// <summary>
    /// One more reader. Only while the caller's own claim still stands - which is what makes it
    /// safe: a lease nobody holds is never retained back to life.
    /// </summary>
    public void Retain()
    {
        if (Interlocked.Increment(ref _holders) <= 1)
            throw new InvalidOperationException("A frame was retained after its last holder let it go.");
    }

    /// <summary>One reader fewer. The last one sends the buffer back for the next frame.</summary>
    public void Release()
    {
        int left = Interlocked.Decrement(ref _holders);

        if (left == 0) _pool?.Return(Buffer);
        else if (left < 0) _pool?.NoteOverRelease();
    }
}

/// <summary>
/// The buffers a recording's frames are copied into, used again instead of made again.
///
/// <b>Why.</b> A frame of 1080-line video is four megabytes, which the runtime puts on the large
/// object heap, and one was allocated for every frame off the receiver: a hundred megabytes a
/// second at 25 fps. Measured on its own, that pattern ran two to four full collections every
/// second and made the copy itself three times slower, because fresh memory has to be faulted in.
/// Reused, it is no collections at all.
///
/// One pool per recording, so nothing outlives the recording that needed it. A lease that is never
/// released - a queue torn down with frames still in it - costs the pool that buffer and nothing
/// else: the collector takes it, and the next frame gets a new one.
/// </summary>
public sealed class FramePool
{
    /// <summary>
    /// Buffers kept waiting for the next frame. More than this only ever exist while they are in
    /// use: the encoder's queue and each ring's hold nine apiece when everything is stalled.
    /// </summary>
    public const int KeepAtMost = 16;

    private readonly ConcurrentStack<byte[]> _free = new();
    private long _made, _reused, _overReleased;
    private int _waiting;

    public FramePool(int frameBytes) => FrameBytes = frameBytes;

    public int FrameBytes { get; }

    /// <summary>Buffers allocated since the recording began. Steady once the pool has warmed up.</summary>
    public long Made => Interlocked.Read(ref _made);

    /// <summary>Frames that went into a buffer used before.</summary>
    public long Reused => Interlocked.Read(ref _reused);

    /// <summary>Buffers idle in the pool now.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>Bytes the pool is holding idle.</summary>
    public long WaitingBytes => (long)Waiting * FrameBytes;

    /// <summary>Releases beyond the last holder. Always nought; anything else is a fault in a caller.</summary>
    public long OverReleased => Interlocked.Read(ref _overReleased);

    /// <summary>
    /// A buffer of exactly <paramref name="bytes"/>, held once by the caller. Pooled when that is
    /// a whole frame; a short read off the receiver gets an array of its own length, as it always
    /// did, because readers go by the array's length.
    /// </summary>
    public FrameLease Rent(int bytes)
    {
        if (bytes != FrameBytes)
            return new FrameLease(GC.AllocateUninitializedArray<byte>(bytes), null);

        if (_free.TryPop(out byte[]? buffer))
        {
            Interlocked.Decrement(ref _waiting);
            Interlocked.Increment(ref _reused);
        }
        else
        {
            // Not cleared: the caller fills every byte of it.
            buffer = GC.AllocateUninitializedArray<byte>(FrameBytes);
            Interlocked.Increment(ref _made);
        }

        return new FrameLease(buffer, this);
    }

    internal void Return(byte[] buffer)
    {
        if (Volatile.Read(ref _waiting) >= KeepAtMost) return;      // the collector's now

        _free.Push(buffer);
        Interlocked.Increment(ref _waiting);
    }

    internal void NoteOverRelease() => Interlocked.Increment(ref _overReleased);
}
