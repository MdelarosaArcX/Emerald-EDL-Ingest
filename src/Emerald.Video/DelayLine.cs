using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Emerald.Core;

namespace Emerald.Video;

public sealed class DelayLineException : Exception
{
    public DelayLineException(string message) : base(message) { }
}

/// <summary>Why a read did not produce a frame.</summary>
public enum DelayReadResult
{
    Ok,

    /// <summary>The writer has not reached this frame yet.</summary>
    NotYetWritten,

    /// <summary>The writer has been all the way round and overwritten it.</summary>
    Lapped,

    /// <summary>Sealed, and this frame is past the end.</summary>
    EndOfLine,

    /// <summary>Compressed (see DelayOverflow) and not decoded yet: ask again shortly, and hold the picture meanwhile.</summary>
    NotReady,

    /// <summary>It left memory and was lost: there is nothing to wait for. Hold a picture in its place and go on.</summary>
    Missing,
}

/// <summary>
/// One frame read out of the line. Owned by the reader and reused, so a failed read never
/// has to leave a half-copied frame anywhere.
/// </summary>
public sealed class DelayFrameBuffer
{
    public DelayFrameBuffer(int frameBytes, int audioCapacityBytes)
    {
        Video = new byte[frameBytes];
        Audio = new byte[audioCapacityBytes];
    }

    public byte[] Video { get; }
    public byte[] Audio { get; }
    public int VideoBytes { get; internal set; }
    public int AudioBytes { get; internal set; }

    /// <summary>Channels interleaved in <see cref="Audio"/>: two for every pair the recording carries.</summary>
    public int AudioChannels { get; internal set; } = 2;

    public long Sequence { get; internal set; } = -1;

    /// <summary>
    /// Which frame of the <i>recording</i> this is, counted from the recorder's first frame.
    ///
    /// Not the same number as <see cref="Sequence"/>, which counts from the first frame this
    /// line took. A line opened part-way through a recording - arming, or disarming and arming
    /// again, ten minutes in - starts its own count at nought while the recording is already
    /// ten minutes along, and anything placing the transmitter on the recording needs this one.
    /// </summary>
    public long RecordingFrame { get; internal set; } = -1;

    /// <summary>True when the receiver genuinely carried audio, rather than silence standing in.</summary>
    public bool AudioWasReal { get; internal set; }
}

/// <summary>
/// The delay itself: a ring of raw frames on disk, written at one end by the recorder and
/// read at the other, a fixed number of frames behind, by the transmitter.
///
/// <b>Why a ring of raw frames.</b> A delay line's whole job is to hold N frames and hand
/// them back in order. Encoding them first would put a codec in the path to air - generation
/// loss on every frame, and a decoder's buffering deciding how long the delay actually is.
/// Holding them as they arrived means the bytes the receiver produced are the bytes the
/// transmitter sends, and the delay is a number of frames rather than an emergent property.
///
/// <b>Why RAM.</b> The ring is 104 MB/s in and, on air, 104 MB/s out, for as long as it runs. On this
/// plant's delay drive (a Crucial BX500) that held for about 38 GB, some six minutes, and from then
/// on the drive stalled for around ten seconds in every 43 - 100% busy at 40 MB/s - whether the ring
/// went through the Windows cache or past it (measured 2026-11-23). In RAM the same plant lost no
/// frame and did not touch the disk. So the newest frames, enough for the delay and as much insert
/// room as memory allows, are held in a ring in RAM, locked so Windows cannot page it out
/// (see <see cref="Create(string, CaptureFormat, TimeSpan, TimeSpan, TimeSpan)"/>).
///
/// <b>Why a file as well.</b> A minute of 1080p at 25 is six gigabytes, and not every machine has the
/// memory for its delay. When it does not, the ring is the file, and the log says so.
///
/// <b>Inserts.</b> An insert makes the delay longer for the rest of the transmission, not only while
/// it plays. What it adds is compressed (DelayOverflow), a seventh of the size, in memory taken as it is
/// needed and given back; without that, an insert that would not fit is refused (<see cref="RoomFrames"/>).
///
/// <b>Why it trims what it has written.</b> Left to decide, the operating system keeps all of it:
/// a ready line nothing is reading sat at 41 GB of working set, for frames nobody may ever ask
/// for. So the writer gives pages back once they are behind everything that could want them
/// soon. They stay in the file, and usually in the standby list, so a read still finds them.
///
/// <b>How it stays safe.</b> There is no lock on the hot path; a four-megabyte copy under a
/// lock would put the transmitter's cadence at the mercy of the writer. Instead each slot
/// carries its own sequence number, written last and checked twice by the reader: once
/// before copying and once after. If it changed in between, the writer came round mid-copy
/// and the frame is discarded rather than transmitted torn. The reader alternates between
/// two buffers, so a discarded frame is never the one it was about to fall back on.
/// </summary>
public sealed class DelayLine : IDelayLine, IDisposable
{
    // ------------------------------------------------------------------ layout

    private const int FileHeaderBytes = 4096;
    internal const int SlotHeaderBytes = 64;
    private const long Alignment = 4096;

    // Slot header offsets.
    internal const int OffSequence = 0;      // long, written last
    internal const int OffVideoBytes = 8;    // int
    internal const int OffAudioBytes = 12;   // int
    internal const int OffFlags = 16;        // int
    internal const int OffRecordingFrame = 24; // long
    private const int FlagAudioReal = 1;

    // Set on every slot a frame was written to. A slot of the file no frame was ever spilled to
    // reads as zeros - sequence 0 - and without this would pass for frame 0.
    private const int FlagWritten = 2;

    // How many channels the sound has, in the flags: it travels with them through the compressed
    // part of the delay as well. Nothing there is stereo.
    private const int ChannelsShift = 8;

    private static int FlagsFor(bool audioReal, int channels) =>
        (audioReal ? FlagAudioReal : 0) | FlagWritten | (Math.Clamp(channels, 0, MaxAudioChannels) << ChannelsShift);

    private static int ChannelsIn(int flags) => (flags >> ChannelsShift & 0xFF) is var channels and > 0 ? channels : 2;

    // What stays in the working set: seconds behind the writer. A reader within NearReaderSeconds
    // of the writer keeps its distance and a margin resident too, so it never walks into pages
    // trimmed just ahead of it.
    //
    // A reader further back - a transmitter a whole delay behind - no longer does. Keeping
    // everything between it and the writer resident is what put 12.7 GB in Emerald's working set
    // with a 2-minute delay on air (measured 2026-11-12: 14.3 GB working set, 745 MB of it
    // Emerald's own). Its frames are written to the file and left in the standby cache instead
    // (see FlushLoop), where they cost a soft fault to read back and count as available memory,
    // and the pages it has read are trimmed again behind it.
    private const int ResidentSeconds = 4;
    private const int ReaderMarginSeconds = 2;
    private const int NearReaderSeconds = 15;

    // How often the flusher looks, and how much it hands the disk at a time.
    private const int FlushEveryMs = 250;
    private const int FlushChunkFrames = 25;

    // Disk given back at a time behind what is kept (ReleaseBehind), and how far clear of the
    // writer's next slots that stays: the writer must never be writing a slot as it is zeroed.
    // Small, because on an SSD each chunk is a TRIM and the drive may be busy with it for a while.
    private const int ReleaseChunkFrames = 25;
    private const int WrapMarginSeconds = 5;

    // Disk is given back only when the volume is short of it: less free than the ring's whole
    // length and this much besides. Asked of the volume at most this often.
    private const long ReleaseFreeMarginBytes = 20L * 1024 * 1024 * 1024;
    private const long FreeCheckEveryMs = 10_000;

    // FSCTL_SET_SPARSE and FSCTL_SET_ZERO_DATA.
    private const uint SetSparseCode = 0x000900C4;
    private const uint SetZeroDataCode = 0x000980C8;

    // A reader that has not been heard from for this long no longer holds its pages. Long enough
    // to ride out a stalled transmitter, short enough that a scrub does not hold them for good.
    private const long ReaderLapseMs = 2000;

    /// <summary>The most channels a frame's sound can have: the eight pairs an SDI signal carries.</summary>
    public const int MaxAudioChannels = 16;

    /// <summary>
    /// A frame of sound on every channel an SDI signal carries, and 64 samples over. The SDK returns
    /// whatever audio the slot carried, which is near but not always equal to one frame's worth, so
    /// the actual length is recorded per slot rather than assumed.
    ///
    /// It was four frames of stereo, which four recorded pairs filled to the byte: one sample more,
    /// or a fifth pair, and the end of every frame's sound was cut off.
    /// </summary>
    public static int AudioCapacityFor(int frameRate) =>
        (48000 / Math.Max(1, frameRate) + 64) * 2 * MaxAudioChannels;

    /// <summary>As much of a frame's sound as a slot takes, in whole samples of every channel.</summary>
    private int KeptAudio(byte[]? audio, int audioBytes, int channels)
    {
        if (audio is null || audioBytes <= 0) return 0;

        int kept = Math.Min(audioBytes, _audioCapacity);
        return kept < audioBytes ? kept - kept % (2 * (channels > 0 ? channels : 2)) : kept;
    }

    /// <summary>
    /// Bytes per slot, rounded up to a page. The alignment is not cosmetic: it keeps the
    /// writer's copy off any page the reader is part-way through.
    /// </summary>
    public static long SlotStrideFor(CaptureFormat format)
    {
        long raw = SlotHeaderBytes + format.FrameBytes + AudioCapacityFor(format.FrameRate);
        return (raw + Alignment - 1) / Alignment * Alignment;
    }

    /// <summary>
    /// Slots for a delay: the delay itself plus two seconds of guard. The guard is what keeps
    /// the writer from arriving at the slot the reader is reading; without it the two would
    /// meet exactly at the design point.
    /// </summary>
    public static long SlotCountFor(CaptureFormat format, TimeSpan delay)
    {
        long wanted = (long)Math.Ceiling(delay.TotalSeconds * format.FrameRate);
        return wanted + GuardFramesFor(format.FrameRate);
    }

    private static long GuardFramesFor(int frameRate) => Math.Max(2L * frameRate, 50);

    /// <summary>Frames that may wait between the receiver and the ring's writer: three seconds' worth, at least eight.</summary>
    public static int QueueDepthFor(int frameRate) => Math.Max(8, frameRate * QueueSeconds);

    private const int QueueSeconds = 3;

    public static long SizeFor(CaptureFormat format, TimeSpan delay) =>
        FileHeaderBytes + SlotCountFor(format, delay) * SlotStrideFor(format);

    // ------------------------------------------------------------------ state

    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _view;

    // Direct: the file is read and written past the Windows cache, and nothing is mapped (see IsDirect).
    private readonly bool _direct;
    private unsafe byte* _directWrite;
    private unsafe byte* _directRead;
    private readonly object _directReadLock = new();
    private ReadAhead? _readAhead;
    private readonly object _readAheadLock = new();
    private readonly FileStream _file;
    private readonly long _stride;
    private readonly int _audioCapacity;

    private unsafe byte* _base;

    private readonly System.Collections.Concurrent.BlockingCollection<CapturedFrame>? _queue;
    private readonly Thread? _writer;

    private long _written;
    private long _accepted;
    private long _sealedAt = -1;
    private long _lastWriteTicks;
    private long _dropped;
    private long _dropSaidAt;
    private long _trimmedThrough;
    private long _readerTrimmedThrough;
    private long _flushedThrough;
    private long _releasedThrough;
    private readonly bool _sparse;
    private Func<long>? _keep;
    private readonly object _holdsLock = new();
    private readonly List<DelayHold> _holds = new();
    private readonly ManualResetEventSlim _stopFlushing = new(false);
    private readonly Thread _flusher;
    private readonly IDisposable _dirtyGauge;
    private long _readerDistance;
    private long _readerSeenAt;
    private int _refs = 1;
    private int _disposed;
    private readonly IDisposable _gauge;
    private readonly IDisposable _heldGauge;

    public string Path { get; }

    /// <summary>
    /// The drive the ring is written to, as an operator would recognise it - "D: (Media, SSD)" -
    /// so a line that cannot keep up says which disk it is waiting on.
    /// </summary>
    public string Disk => _disk ??= DescribeDisk(Path);

    private string? _disk;

    public static string DescribeDisk(string path)
    {
        string root;

        try
        {
            root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path)) ?? path;
        }
        catch
        {
            return path;
        }

        string name = root.TrimEnd('\\', '/');

        try
        {
            var drive = new DriveInfo(root);
            string label = drive.IsReady && drive.VolumeLabel.Length > 0 ? drive.VolumeLabel : "";
            string kind = drive.DriveType switch
            {
                DriveType.Fixed => "local",
                DriveType.Network => "network",
                DriveType.Removable => "removable",
                _ => drive.DriveType.ToString().ToLowerInvariant(),
            };

            return label.Length > 0 ? $"{name} ({label}, {kind})" : $"{name} ({kind})";
        }
        catch
        {
            return name;
        }
    }

    public int Width { get; }
    public int Height { get; }
    public int FrameRate { get; }
    public int FrameBytes { get; }
    public long SlotCount { get; }
    public long TargetFrames { get; }
    public CaptureFormat Format { get; }

    public long FramesWritten => Volatile.Read(ref _written);
    public bool IsSealed => Volatile.Read(ref _sealedAt) >= 0;
    public long SealedAt => Volatile.Read(ref _sealedAt);

    /// <summary>Frames the writer had to throw away because the ring could not take them.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Frames before this one have been given back to the operating system. They can still be read.</summary>
    public long TrimmedThrough => Volatile.Read(ref _trimmedThrough);

    /// <summary>
    /// Frames before this one have been handed to the disk. What lies between this and
    /// <see cref="FramesWritten"/> is only in memory, and is what the ring costs in RAM beyond its
    /// few resident seconds.
    /// </summary>
    public long FlushedThrough => Volatile.Read(ref _flushedThrough);

    /// <summary>
    /// Frames before this one have had their disk space given back. Nothing reads them: a read
    /// of one is <see cref="DelayReadResult.Lapped"/>, as if the ring had come round.
    /// </summary>
    public long ReleasedThrough => Volatile.Read(ref _releasedThrough);

    /// <summary>
    /// The ring file is sparse, so it takes disk only for the frames it is holding, not for its
    /// whole length. False on a volume that cannot do that; such a ring takes its full size.
    /// </summary>
    public bool IsSparse => _sparse;

    /// <summary>
    /// The delay is held on disk and read and written directly, past the Windows cache: one steady
    /// stream each way, with a few seconds read ahead of the transmitter into memory of its own.
    /// False for a line held in RAM, or one through the mapped file.
    /// </summary>
    public bool IsDirect => _direct;

    /// <summary>
    /// The oldest frame that can still be read: the ring's far end, the last frame given back
    /// to the disk, or the last frame let go from memory without being spilled, whichever is newest.
    /// </summary>
    public long OldestHeld
    {
        get
        {
            long written = Volatile.Read(ref _written);

            // Let go is decided a moment before a frame leaves memory; until it does, it reads.
            long lost = Math.Min(Volatile.Read(ref _lostThrough), Math.Max(0, written - _ramSlots));

            return Math.Max(Math.Max(Math.Max(0, written - SlotCount), Volatile.Read(ref _releasedThrough)), lost);
        }
    }

    /// <summary>
    /// Frames held in memory: the newest this many are written to and read from RAM, and the disk
    /// sees a frame only if something still wants it once it leaves. 0 for a line on disk alone.
    /// </summary>
    public long MemorySlots => _ramSlots;

    // How far short of the end of memory a transmitter must stay for nothing to be written out: a
    // frame is decided SpillLeadSeconds before it leaves, and a hold keeps ReaderMarginSeconds
    // behind itself. A second on top for a transmitter that repeats a frame or two.
    private const int MemoryGuardSeconds = SpillLeadSeconds + ReaderMarginSeconds + 1;

    /// <summary>
    /// How far behind the recorder a transmitter on this line may be, in frames: the delay and every
    /// insert it has grown by. Held in RAM, as far as memory reaches without a frame being written
    /// out; on disk, the ring's length less its guard.
    /// </summary>
    public long RoomFrames
    {
        get
        {
            long ring = Math.Max(0, SlotCount - GuardFramesFor(FrameRate));

            if (_ramSlots <= 0 || _ramSlots >= SlotCount) return ring;
            if (!OverflowReady) return MemoryRoomFrames;

            // What is kept raw, and as much again as memory could hold compressed just now.
            return Math.Min(ring, MemoryRoomFrames + _overflow!.RoomFrames);
        }
    }

    /// <summary>Memory to ask for so that a delay of this much is held with nothing to spare: the delay and the guard.</summary>
    public static TimeSpan MemoryFor(TimeSpan delay) => delay + TimeSpan.FromSeconds(MemoryGuardSeconds);

    /// <summary>
    /// Whether a line of this format can grow past the memory kept for it (see DelayOverflow), trying it out
    /// the first time, which is well under a second. If not, it needs its insert room in memory from the start.
    /// </summary>
    public static bool CanGrow(string? ffmpeg, CaptureFormat format, TimeSpan most)
    {
        if (ffmpeg is null || !DelayOverflow.Supports(format)) return false;

        Task<string?> probe = DelayOverflow.Probe(ffmpeg, format);

        try { return probe.Wait(most) && probe.Result is null; }
        catch (AggregateException) { return false; }
    }

    /// <summary>Memory taken just now for what inserts have added to the delay.</summary>
    public long GrownBytes => _overflow?.Bytes ?? 0;

    /// <summary>
    /// How far behind the recorder a transmitter may be with every frame still raw and in memory - as far
    /// as a transmission can start, and as much as an idle line keeps. Beyond it, up to
    /// <see cref="RoomFrames"/>, frames are compressed (see DelayOverflow).
    /// </summary>
    public long MemoryRoomFrames =>
        _ramSlots > 0 && _ramSlots < SlotCount
            ? Math.Max(0, _ramSlots - (long)Math.Max(1, FrameRate) * MemoryGuardSeconds)
            : Math.Max(0, SlotCount - GuardFramesFor(FrameRate));

    private DelayOverflow? _overflow;
    private Task<string?>? _overflowProbe;
    private Thread? _overflowThread;

    /// <summary>Frames pushed past the end of memory can be compressed: proved on this machine, and not since gone wrong.</summary>
    public bool OverflowReady =>
        _overflow is { Faulted: false } && _overflowProbe is { IsCompletedSuccessfully: true, Result: null };

    /// <summary>Frames compressed so far; 0 until an insert pushes the delay past memory.</summary>
    public long Compressed => _overflow?.Encoded ?? 0;

    /// <summary>Frames that should have been compressed and were lost.</summary>
    public long CompressedLost => _overflow?.Missing ?? 0;

    /// <summary>Waits for the overflow to have been tried out. False when there is none, or it did not work, or took too long.</summary>
    public bool WaitForOverflow(TimeSpan most)
    {
        try { _overflowProbe?.Wait(most); }
        catch (AggregateException) { }

        return OverflowReady;
    }

    /// <summary>
    /// Lets a line held in RAM take inserts past the end of memory, compressed. Straight after it is opened,
    /// before any frame. Nothing is started, and nothing taken, until one does.
    /// </summary>
    private void OpenOverflow(string ffmpeg)
    {
        if (_ramSlots <= 0 || _ramSlots >= SlotCount) return;

        if (!DelayOverflow.Supports(Format))
        {
            NoOverflow($"{Format.Name} is not a picture it is made for");
            return;
        }

        _overflow = new DelayOverflow(this, Path, ffmpeg, Format, _stride);
        _overflowProbe = DelayOverflow.Probe(ffmpeg, Format);
        _overflowProbe.ContinueWith(t =>
        {
            if (t.Result is { } why) NoOverflow(why);
            else
                ActivityLog.Shared.Info(LogSource.TidalLock,
                    $"DELAY  inserts may grow the delay to {Describe(TimeSpan.FromSeconds(RoomFrames / (double)Math.Max(1, FrameRate)))}, taking about " +
                    $"{DelayOverflow.StrideFor(Format) * (double)FrameRate / 1e6:F0} MB of memory for each second they add.",
                    @event: "delay.overflowready", file: Path);
        }, TaskScheduler.Default);

        _overflowThread = new Thread(OverflowLoop) { IsBackground = true, Name = "tidal overflow" };
        _overflowThread.Start();
    }

    private void NoOverflow(string why) =>
        ActivityLog.Shared.Warn(LogSource.TidalLock,
            $"DELAY  inserts are limited to what memory holds: the delay cannot be compressed past it ({why}).",
            @event: "delay.overflowoff", file: Path);

    /// <summary>
    /// Hands frames about to leave memory, and still wanted, to the overflow. On its own thread, which waits for
    /// the encoder: the writer does not wait for this one (see <see cref="SpillThrough"/>).
    /// </summary>
    private unsafe void OverflowLoop()
    {
        byte* raw = (byte*)NativeMemory.AlignedAlloc((nuint)_stride, DirectAlignment);

        try
        {
            while (!_stopFlushing.Wait(40))
            {
                while (!_stopFlushing.IsSet)
                {
                    long upTo = Volatile.Read(ref _written) - _ramSlots + (long)Math.Max(1, FrameRate) * SpillLeadSeconds;
                    long seq;
                    bool take, lose = false;

                    lock (_spillLock)
                    {
                        seq = _spilledThrough;
                        if (seq >= upTo) break;

                        take = seq >= KeepFloor(Volatile.Read(ref _written));

                        if (take && !OverflowReady)
                        {
                            take = false;
                            lose = true;
                        }
                        else if (take)
                        {
                            byte* slot = RamSlotAt(seq);
                            Buffer.MemoryCopy(slot, raw, _stride, _stride);
                            take = *(long*)(raw + OffSequence) == seq;
                            lose = !take;
                        }
                        else
                        {
                            Volatile.Write(ref _lostThrough, seq + 1);
                        }

                        Volatile.Write(ref _spilledThrough, seq + 1);
                    }

                    if (take) _overflow!.Submit(seq, raw);
                    else if (lose) _overflow!.Lose(seq);
                }

                _overflow!.Tidy(KeepFloor(Volatile.Read(ref _written)));
            }
        }
        catch (Exception ex)
        {
            ActivityLog.Shared.Error(LogSource.TidalLock, $"DELAY  compressing the delay stopped: {ex.Message}",
                                     @event: "delay.overflowfault", file: Path);
        }
        finally
        {
            NativeMemory.AlignedFree(raw);
        }
    }

    /// <summary>True once the memory ring is locked in RAM, so Windows cannot page it out.</summary>
    public bool MemoryLocked => Volatile.Read(ref _ramLocked) != 0;

    /// <summary>Frames written to the disk because they left memory still wanted. 0 in normal running.</summary>
    public long Spilled => Interlocked.Read(ref _spilled);

    /// <summary>
    /// How far behind the writer the line keeps frames on disk when no transmitter is holding
    /// any further back, asked afresh every quarter second. <see cref="TargetFrames"/> until set.
    ///
    /// A line kept ready is made big enough for any delay and the inserts that can grow it, but
    /// until something is armed only the longest delay a transmitter could start with is worth
    /// keeping. The rest of its length is given back to the disk, and taken again only while
    /// a transmitter is actually that far behind.
    /// </summary>
    public void KeepBehind(Func<long> frames) => Volatile.Write(ref _keep, frames);

    /// <summary>
    /// A claim on the frames from <paramref name="sequence"/> on, for a transmitter: nothing at
    /// or after it is given back to the disk until the claim is moved on or disposed. Unlike a
    /// read, it holds while the transmitter is playing something else - an insert - and will
    /// come back to the ring where it left off.
    /// </summary>
    public DelayHold Hold(long sequence)
    {
        var hold = new DelayHold(this, sequence);
        lock (_holdsLock) _holds.Add(hold);
        return hold;
    }

    internal void Unhold(DelayHold hold)
    {
        lock (_holdsLock) _holds.Remove(hold);
    }

    /// <summary>Channels of sound in the last frame written, two for every pair; zero before any.</summary>
    public int AudioChannels => Volatile.Read(ref _audioChannels);
    private int _audioChannels;

    /// <summary>Set when a write failed. The line seals itself rather than failing the recording.</summary>
    public string? Fault { get; private set; }

    /// <summary>Milliseconds since the recorder last committed a frame; 0 before the first.</summary>
    public long StalledMs
    {
        get
        {
            long last = Volatile.Read(ref _lastWriteTicks);
            if (last == 0) return 0;

            return (long)((System.Diagnostics.Stopwatch.GetTimestamp() - last) * 1000.0
                          / System.Diagnostics.Stopwatch.Frequency);
        }
    }

    private unsafe DelayLine(string path, FileStream file, MemoryMappedFile? mmf,
                             MemoryMappedViewAccessor? view, CaptureFormat format,
                             long slotCount, long stride, long targetFrames, bool sparse, long ramSlots,
                             long ramRequired)
    {
        Path = path;
        _sparse = sparse;
        _file = file;
        _mmf = mmf;
        _view = view;
        _direct = view is null;
        Format = format;
        Width = format.Width;
        Height = format.Height;
        FrameRate = format.FrameRate;
        FrameBytes = format.FrameBytes;
        SlotCount = slotCount;
        TargetFrames = targetFrames;
        _stride = stride;
        _audioCapacity = AudioCapacityFor(format.FrameRate);

        if (_direct)
        {
            // One slot's worth each, sector-aligned, for the writer and for reads not served by
            // the read-ahead. Native rather than managed: the disk reads into it directly.
            _directWrite = (byte*)NativeMemory.AlignedAlloc((nuint)stride, DirectAlignment);
            _directRead = (byte*)NativeMemory.AlignedAlloc((nuint)stride, DirectAlignment);
        }
        else
        {
            _view!.SafeMemoryMappedViewHandle.AcquirePointer(ref _base);
        }

        // Mapped, not private: only the part not yet trimmed shows in the working set. Direct, nothing is mapped.
        long mapped = _direct ? 0 : FileHeaderBytes + slotCount * stride;
        _gauge = MemoryGauges.Register("Delay", $"ring mapped ({System.IO.Path.GetFileName(path)})", () => mapped);

        // A target rather than a measurement: a reader's cold frames come back in until they are trimmed again.
        _heldGauge = MemoryGauges.Register("Delay", $"ring held in memory, target ({System.IO.Path.GetFileName(path)})",
            () => _direct ? (_readAhead?.Bytes ?? 0) : Math.Min(FramesWritten - TrimmedThrough, slotCount) * stride);

        _dirtyGauge = MemoryGauges.Register("Delay", $"ring not yet on disk ({System.IO.Path.GetFileName(path)})",
            () => _direct ? 0 : Math.Min(Math.Max(0, FramesWritten - FlushedThrough), slotCount) * stride);

        OpenMemoryRing(Math.Min(ramSlots, slotCount), Math.Min(ramRequired, slotCount));

        long ramBytes = _ramSlots * stride;
        _ramGauge = MemoryGauges.Register("Delay", $"ring in RAM ({System.IO.Path.GetFileName(path)})", () => ramBytes);

        _flusher = new Thread(FlushLoop)
        {
            IsBackground = true,
            Name = "tidal delay flusher",
            Priority = ThreadPriority.BelowNormal,
        };

        _flusher.Start();

        // Three seconds of slack between the receiver and the disk. It was eight frames, the depth
        // the recorder allows its encoder - but the encoder's queue protects the recording, which
        // must never wait, while this one only protects the delay, and a third of a second was
        // too little to ride out the disk being busy for a moment: every hiccup longer than that
        // was frames turned away and missing on air. Three seconds rides out a short pause; it does
        // not ride out a drive that stalls for ten, which is why the ring is in RAM when it can be.
        // A frame waiting here is a buffer held from the capture pool, so three seconds cost about
        // 300 MB at 1080p25 - only while the disk is behind, and given back as it catches up.
        _queue = new System.Collections.Concurrent.BlockingCollection<CapturedFrame>(QueueDepthFor(format.FrameRate));

        _writer = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "tidal delay writer",
        };

        _writer.Start();
    }

    // ------------------------------------------------------------------ creation

    /// <summary>
    /// Checks a delay can be allocated before anything commits to it. Returns false with a
    /// sentence naming the sizes, because "it did not work" is no use to an operator standing
    /// in front of a transmitter.
    /// </summary>
    public static bool Validate(string folder, CaptureFormat format, TimeSpan delay,
                                out long sizeBytes, out string? problem)
    {
        sizeBytes = SizeFor(format, delay);
        problem = null;

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            problem = $"The delay line needs a folder at {folder}, and it could not be created: {ex.Message}";
            return false;
        }

        try
        {
            string? root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(folder));

            if (root is { Length: > 0 })
            {
                long free = new DriveInfo(root).AvailableFreeSpace;

                // Two gigabytes of headroom, so filling the ring cannot itself fill the volume.
                if (free < sizeBytes + 2_000_000_000L)
                {
                    problem = $"The delay line needs {Describe(sizeBytes)} on {root} for " +
                              $"{Describe(delay)} of {format.Name}, and only {Describe(free)} is free.";
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            problem = $"The delay line's disk could not be checked: {ex.Message}";
            return false;
        }

        return true;
    }

    /// <summary>A line through a memory-mapped file and the Windows cache - the engine RAM mode spills into.</summary>
    public static DelayLine Create(string folder, CaptureFormat format, TimeSpan delay) =>
        Open(folder, format, delay, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, direct: false);

    /// <summary>The same, holding <paramref name="headroom"/> more than the delay for inserts.</summary>
    public static DelayLine Create(string folder, CaptureFormat format, TimeSpan delay, TimeSpan headroom) =>
        Open(folder, format, delay, headroom, TimeSpan.Zero, TimeSpan.Zero, direct: false);

    /// <summary>What a line held in RAM still wants free on its drive. Nothing is expected to be written to it.</summary>
    private const long SpillRoomBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>The same, needing the delay itself to fit in RAM for RAM to be used.</summary>
    public static DelayLine Create(string folder, CaptureFormat format, TimeSpan delay, TimeSpan headroom,
                                   TimeSpan memory) =>
        Create(folder, format, delay, headroom, memory, required: delay);

    /// <summary>
    /// A line that fills to <paramref name="delay"/> but has room for <paramref name="headroom"/> more:
    /// <b>in RAM</b>, up to <paramref name="memory"/> of it, when memory can hold at least
    /// <paramref name="required"/> and the guard behind it; otherwise <b>on disk</b>, in the folder given,
    /// with a warning. Decided once, as the line opens: a delay already on air is never moved.
    ///
    /// For a transmitter whose delay grows while it runs - Live Edit's, where an insert holds
    /// the recording back for as long as it plays. The ring cannot be enlarged once it exists,
    /// so the room has to be made up front; the target, and so the countdown, is still only the
    /// delay asked for. In RAM the room is what memory gave, which can be less than was asked for:
    /// <see cref="RoomFrames"/>.
    ///
    /// <b>On disk</b> it is read and written directly, past the Windows cache, with a few seconds
    /// read ahead of the transmitter into memory of its own (see <see cref="IsDirect"/>). That does
    /// not make a slow drive fast: one that stalls for longer than those seconds still loses frames.
    /// </summary>
    /// <param name="required">
    /// The least that must fit in RAM: the furthest back a transmitter could start. Not always
    /// <paramref name="delay"/> - the ready line's is its whole length, inserts and all.
    /// </param>
    /// <param name="ffmpeg">To compress with, for inserts that grow the delay past memory. Null: they are refused.</param>
    public static DelayLine Create(string folder, CaptureFormat format, TimeSpan delay, TimeSpan headroom,
                                   TimeSpan memory, TimeSpan required, string? ffmpeg = null)
    {
        if (memory > TimeSpan.Zero)
        {
            DelayLine held = Open(folder, format, delay, headroom, memory, required, direct: false);

            if (held.MemorySlots > 0)
            {
                if (ffmpeg is not null) held.OpenOverflow(ffmpeg);
                return held;
            }

            // Not enough memory, or another line has it - said by OpenMemoryRing. Nothing has been
            // written to it yet.
            held.Dispose();
        }

        DelayLine line = Open(folder, format, delay, headroom, TimeSpan.Zero, TimeSpan.Zero, direct: true);

        if (memory > TimeSpan.Zero)
            ActivityLog.Shared.Warn(LogSource.TidalLock,
                $"DELAY  the delay is held on disk {line.Disk}, not in RAM, so every frame is written to it and read " +
                "back. If that drive cannot keep up, frames will be missing on air. Free some memory, or choose a " +
                "shorter delay, and start the recording again.",
                @event: "delay.ondisk", file: line.Path);

        return line;
    }

    /// <summary>Free bytes on the folder's drive, or -1 when it cannot be read.</summary>
    private static long FreeBytes(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string? root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(folder));
            return root is { Length: > 0 } ? new DriveInfo(root).AvailableFreeSpace : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return -1;
        }
    }

    // FILE_FLAG_NO_BUFFERING: reads and writes go to the disk, not through the system cache. They
    // must then be whole sectors at sector-aligned offsets from sector-aligned memory - which a
    // slot, page-aligned and a whole number of pages long, already is.
    private const FileOptions NoBuffering = (FileOptions)0x20000000;
    private const int DirectAlignment = 4096;

    private static DelayLine Open(string folder, CaptureFormat format, TimeSpan delay, TimeSpan headroom,
                                  TimeSpan memory, TimeSpan required, bool direct)
    {
        TimeSpan holds = delay + (headroom > TimeSpan.Zero ? headroom : TimeSpan.Zero);
        long size;

        if (memory > TimeSpan.Zero)
        {
            // Held in RAM, the file takes only frames pushed out of memory - none, while transmitters
            // keep to RoomFrames - so it need not fit the whole ring.
            size = SizeFor(format, holds);
            long free = FreeBytes(folder);

            if (free >= 0 && free < SpillRoomBytes)
                throw new DelayLineException(
                    $"The delay line needs at least {Describe(SpillRoomBytes)} free on {DescribeDisk(folder)}, " +
                    $"even held in RAM, and only {Describe(free)} is free.");
        }
        else if (!Validate(folder, format, holds, out size, out string? problem))
        {
            throw new DelayLineException(problem!);
        }

        long stride = SlotStrideFor(format);
        long slots = SlotCountFor(format, holds);
        long target = (long)Math.Ceiling(delay.TotalSeconds * format.FrameRate);

        string path = System.IO.Path.Combine(
            folder, $"tidal-{Environment.ProcessId}-{Guid.NewGuid():N}.ring");

        FileStream? file = null;
        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? view = null;

        try
        {
            if (direct)
            {
                file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                                      0, FileOptions.WriteThrough | NoBuffering);

                // Sparse, as below: the ring takes disk for the frames it holds.
                bool directSparse = SetSparse(file);
                file.SetLength(size);

                var direct_ = new DelayLine(path, file, null, null, format, slots, stride, target, directSparse, 0, 0);
                direct_.WriteFileHeader();
                return direct_;
            }

            file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                                  4096, FileOptions.RandomAccess);
            // Sparse, so the ring takes disk for the frames it holds and not for its whole length:
            // a ready line is sized for the longest delay plus every insert, 41 GB at 420 s, and
            // mostly holds the delay alone. Validate has already checked the whole length fits.
            // It gives that disk back only when the volume runs short (see ReleaseBehind).
            bool sparse = SetSparse(file);
            file.SetLength(size);

            if (!sparse)
            {
                // Touch the last byte so the extent is real now. Discovering the volume is full
                // forty minutes into a recording is not a discovery worth making.
                file.Seek(size - 1, SeekOrigin.Begin);
                file.WriteByte(0);
                file.Flush(true);
            }

            mmf = MemoryMappedFile.CreateFromFile(file, null, size, MemoryMappedFileAccess.ReadWrite,
                                                  HandleInheritability.None, leaveOpen: true);
            view = mmf.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);

            long ramSlots = memory > TimeSpan.Zero ? (long)Math.Ceiling(memory.TotalSeconds * format.FrameRate) : 0;

            // The guard as well as what is required: without it a transmitter exactly that far back
            // would have its frames written out.
            long ramRequired = memory > TimeSpan.Zero
                ? (long)Math.Ceiling(required.TotalSeconds * format.FrameRate) + (long)format.FrameRate * MemoryGuardSeconds
                : 0;

            var line = new DelayLine(path, file, mmf, view, format, slots, stride, target, sparse, ramSlots, ramRequired);
            line.WriteFileHeader();
            return line;
        }
        catch (Exception ex)
        {
            view?.Dispose();
            mmf?.Dispose();
            file?.Dispose();
            try { File.Delete(path); } catch { }

            throw new DelayLineException($"The delay line could not be created at {path}: {ex.Message}");
        }
    }

    /// <summary>Enough to identify an orphan on disk, and to know whose it was.</summary>
    private unsafe void WriteFileHeader()
    {
        byte* header = _direct
            ? (byte*)NativeMemory.AlignedAlloc(FileHeaderBytes, DirectAlignment)
            : _base;

        try
        {
            if (_direct) new Span<byte>(header, FileHeaderBytes).Clear();

            ReadOnlySpan<byte> magic = "EMLDLY01"u8;
            for (int i = 0; i < magic.Length; i++) header[i] = magic[i];

            *(int*)(header + 8) = 1;
            *(int*)(header + 12) = Width;
            *(int*)(header + 16) = Height;
            *(int*)(header + 20) = FrameRate;
            *(int*)(header + 24) = FrameBytes;
            *(int*)(header + 28) = _audioCapacity;
            *(long*)(header + 32) = _stride;
            *(long*)(header + 40) = SlotCount;
            *(long*)(header + 48) = DateTime.UtcNow.Ticks;
            *(int*)(header + 56) = Environment.ProcessId;

            if (_direct) RandomAccess.Write(_file.SafeFileHandle, new ReadOnlySpan<byte>(header, FileHeaderBytes), 0);
        }
        finally
        {
            if (_direct) NativeMemory.AlignedFree(header);
        }
    }

    /// <summary>
    /// Deletes rings left behind by processes that are gone. Six gigabytes survives a crash
    /// perfectly well, and three crashes would be eighteen.
    /// </summary>
    public static void SweepOrphans(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;

            foreach (string path in Directory.EnumerateFiles(folder, "tidal-*.ring"))
            {
                string[] parts = System.IO.Path.GetFileNameWithoutExtension(path).Split('-');
                if (parts.Length < 3 || !int.TryParse(parts[1], out int pid)) continue;
                if (pid == Environment.ProcessId) continue;

                try
                {
                    System.Diagnostics.Process.GetProcessById(pid);
                    continue;                              // still running; leave it alone
                }
                catch (ArgumentException)
                {
                    // No such process: the ring outlived whoever made it.
                }

                try { File.Delete(path); } catch { }
            }
        }
        catch
        {
            // Housekeeping must never stop the application starting.
        }
    }

    // ------------------------------------------------------------------ writing

    private unsafe byte* SlotAt(long sequence) =>
        _base + FileHeaderBytes + sequence % SlotCount * _stride;

    /// <summary>
    /// Commits one frame. Called only by the delay line's own writer thread, never by the
    /// capture loop - a four-megabyte copy into a mapped view takes a page fault per page on
    /// first touch, and the receiver has only four slots of headroom to spare.
    ///
    /// Never throws. A ring that cannot be written seals itself and lets the drain finish;
    /// losing the delay must not cost the operator the recording.
    /// </summary>
    /// <param name="recordingFrame">
    /// The frame's place in the recording, from <see cref="CapturedFrame.Sequence"/>. Left out,
    /// the line's own count stands in, which is right only for a line opened as the recording rolled.
    /// </param>
    public unsafe void Write(byte[] video, int videoBytes, byte[]? audio, int audioBytes, bool audioReal,
                             long recordingFrame = -1, int audioChannels = 0)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        try
        {
            long seq = _written;

            Volatile.Write(ref _audioChannels, audioChannels);

            if (_direct)
            {
                WriteDirect(seq, video, videoBytes, audio, audioBytes, audioReal, recordingFrame, audioChannels);
                return;
            }

            // Held in RAM: the frame this slot last held leaves memory now, so it is written to the
            // file first if anything still wants it. Normally the flusher has done that already.
            if (_ram is not null)
            {
                long leaving = seq - _ramSlots;
                if (leaving >= Volatile.Read(ref _spilledThrough)) SpillThrough(leaving + 1);
            }

            byte* slot = _ram is not null ? RamSlotAt(seq) : SlotAt(seq);

            // Mark the slot in flight before touching its payload, so a reader arriving
            // mid-write sees a sequence matching nothing rather than half a frame.
            Volatile.Write(ref *(long*)(slot + OffSequence), -1L);

            int keptVideo = Math.Min(videoBytes, FrameBytes);
            int keptAudio = KeptAudio(audio, audioBytes, audioChannels);
            long frameOfRecording = recordingFrame >= 0 ? recordingFrame : seq;

            if (_ram is not null || !WriteSlotThroughFile(seq, video, keptVideo, audio, keptAudio, audioReal, frameOfRecording, audioChannels))
            {
                // The file write was refused: straight into the mapping, as it always was.
                fixed (byte* src = video)
                    Buffer.MemoryCopy(src, slot + SlotHeaderBytes, FrameBytes, keptVideo);

                if (keptAudio > 0)
                    fixed (byte* src = audio)
                        Buffer.MemoryCopy(src, slot + SlotHeaderBytes + FrameBytes, _audioCapacity, keptAudio);

                *(int*)(slot + OffVideoBytes) = keptVideo;
                *(int*)(slot + OffAudioBytes) = keptAudio;
                *(int*)(slot + OffFlags) = FlagsFor(audioReal, audioChannels);
                *(long*)(slot + OffRecordingFrame) = frameOfRecording;
            }

            // Last, with a release: everything above must be visible before the sequence is.
            Volatile.Write(ref *(long*)(slot + OffSequence), seq);

            Volatile.Write(ref _lastWriteTicks, System.Diagnostics.Stopwatch.GetTimestamp());
            Volatile.Write(ref _written, seq + 1);

            if ((seq + 1) % Math.Max(1, FrameRate) == 0) TrimBehind(seq + 1);
        }
        catch (Exception ex)
        {
            Fault ??= ex.Message;
            Seal();
        }
    }

    /// <summary>
    /// Direct: the whole slot built in the writer's sector-aligned buffer and written to the disk in
    /// one call, which returns once it is there (write-through). Only then is the frame counted as
    /// written, so a reader never reaches a frame that is not yet whole on disk. No in-flight mark:
    /// a reader tells a slot written over while it read from the count of frames written (see
    /// <see cref="ReadDirect"/>), which a direct write cannot race with a mark in the file.
    /// </summary>
    private unsafe void WriteDirect(long seq, byte[] video, int videoBytes, byte[]? audio, int audioBytes,
                                    bool audioReal, long recordingFrame, int audioChannels)
    {
        byte* s = _directWrite;

        int keptVideo = Math.Min(videoBytes, FrameBytes);
        int keptAudio = KeptAudio(audio, audioBytes, audioChannels);

        *(long*)(s + OffSequence) = seq;
        *(int*)(s + OffVideoBytes) = keptVideo;
        *(int*)(s + OffAudioBytes) = keptAudio;
        *(int*)(s + OffFlags) = FlagsFor(audioReal, audioChannels);
        *(long*)(s + OffRecordingFrame) = recordingFrame >= 0 ? recordingFrame : seq;

        fixed (byte* src = video)
            Buffer.MemoryCopy(src, s + SlotHeaderBytes, FrameBytes, keptVideo);

        if (keptAudio > 0)
            fixed (byte* src = audio)
                Buffer.MemoryCopy(src, s + SlotHeaderBytes + FrameBytes, _audioCapacity, keptAudio);

        RandomAccess.Write(_file.SafeFileHandle, new ReadOnlySpan<byte>(s, (int)_stride),
                           FileHeaderBytes + seq % SlotCount * _stride);

        Volatile.Write(ref _lastWriteTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        Volatile.Write(ref _written, seq + 1);

        // On disk the moment the write returned.
        Volatile.Write(ref _flushedThrough, seq + 1);
    }

    /// <summary>The whole slot, built here and written to the file in one call. The writer's thread only.</summary>
    private byte[]? _staging;

    private int _fileWritesBroken;

    /// <summary>
    /// Writes a slot's header, picture and sound to the ring file in one call, rather than copying
    /// them into the mapped view. False if the file would not take it, and the caller copies into
    /// the mapping as before.
    ///
    /// <b>Why.</b> This is what "the disk could not keep up" mostly was. Copying a frame into the
    /// mapping writes over a slot last used a whole ring ago, whose pages have long since been
    /// taken out of memory (see <see cref="TrimBehind"/>). A write into a page that is not in memory
    /// is a page fault, and Windows answers it by <i>reading the old page back from the disk</i>
    /// before letting it be written over - a thousand reads of four kilobytes for every frame,
    /// one after another, on the writer's thread, for data that is about to be thrown away. That
    /// doubled the disk traffic of the ring and made the writer wait on the disk for every page;
    /// once the eight frames of slack between the receiver and the writer were used up, frames
    /// were turned away.
    ///
    /// A file write that covers whole pages needs nothing read: the system cache knows every byte
    /// is about to be replaced. The slot is page-aligned and a whole number of pages long, so the
    /// write covers exactly whole pages. The cache and the mapping are the same pages of the same
    /// file, so a reader of the mapping sees the frame as soon as the write returns; the sequence is
    /// still marked -1 before and set after, through the mapping, so the torn-read rule is unchanged.
    /// </summary>
    private unsafe bool WriteSlotThroughFile(long seq, byte[] video, int videoBytes, byte[]? audio, int audioBytes,
                                             bool audioReal, long recordingFrame, int audioChannels)
    {
        if (Volatile.Read(ref _fileWritesBroken) != 0) return false;

        byte[] staging = _staging ??= GC.AllocateUninitializedArray<byte>((int)_stride);

        fixed (byte* s = staging)
        {
            *(long*)(s + OffSequence) = -1L;
            *(int*)(s + OffVideoBytes) = videoBytes;
            *(int*)(s + OffAudioBytes) = audioBytes;
            *(int*)(s + OffFlags) = FlagsFor(audioReal, audioChannels);
            *(long*)(s + OffRecordingFrame) = recordingFrame;
        }

        Buffer.BlockCopy(video, 0, staging, SlotHeaderBytes, videoBytes);

        // Past the picture and the sound the slot holds whatever an earlier frame left there, as it
        // always did: readers go by the lengths in the header, never by the slot's size.
        if (audioBytes > 0) Buffer.BlockCopy(audio!, 0, staging, SlotHeaderBytes + FrameBytes, audioBytes);

        try
        {
            long offset = FileHeaderBytes + seq % SlotCount * _stride;
            RandomAccess.Write(_file.SafeFileHandle, staging, offset);
            return true;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _fileWritesBroken, 1);

            ActivityLog.Shared.Warn(LogSource.TidalLock,
                $"The delay ring at {Path} could not be written as a file ({ex.Message}); writing it through " +
                "memory instead, which is slower once the ring has wrapped.",
                @event: "delay.filewritefailed", file: Path);

            return false;
        }
    }

    /// <summary>
    /// Gives back the pages of frames nothing is about to read. On the writer's thread, once a
    /// second of frames: a frame took about half a millisecond to trim when measured, so a second's
    /// worth at 25 fits well inside the writer's slack.
    ///
    /// The mark follows the target down as well as up. A reader armed far behind faults frames
    /// back in that were already trimmed, and moving the mark back with it is what lets those
    /// be trimmed again once the reader has passed them.
    /// </summary>
    private unsafe void TrimBehind(long written)
    {
        int rate = Math.Max(1, FrameRate);
        long keep = (long)rate * ResidentSeconds;

        bool reading = Environment.TickCount64 - Volatile.Read(ref _readerSeenAt) <= ReaderLapseMs;
        long distance = Volatile.Read(ref _readerDistance);
        bool near = distance <= (long)rate * NearReaderSeconds;

        if (reading && near)
            keep = Math.Max(keep, distance + (long)rate * ReaderMarginSeconds);

        // A reader a long way back faults its frames in from the standby cache as it reads them;
        // those go out of the working set again a margin behind it.
        if (reading && !near)
            TrimReaderWake(written - distance - (long)rate * ReaderMarginSeconds, written);

        long target = Math.Max(0, written - keep);
        long from = Math.Max(Volatile.Read(ref _trimmedThrough), written - SlotCount);

        if (target > from)
        {
            long first = from % SlotCount;
            long count = target - from;
            long toEnd = Math.Min(count, SlotCount - first);

            // Round the end of the ring, it is two ranges. False with ERROR_NOT_LOCKED is how
            // VirtualUnlock reports doing this on pages that were never locked, so there is no
            // result to look at. If it did nothing, the pages stay resident and nothing is lost.
            VirtualUnlock(_base + FileHeaderBytes + first * _stride, (nuint)(toEnd * _stride));

            if (count > toEnd)
                VirtualUnlock(_base + FileHeaderBytes, (nuint)((count - toEnd) * _stride));
        }

        Volatile.Write(ref _trimmedThrough, target);
    }

    /// <summary>
    /// Takes the frames a far reader has already read back out of the working set. At most a few
    /// seconds at a time - the reader moves a second a second - and never anything the ring no
    /// longer holds.
    /// </summary>
    private unsafe void TrimReaderWake(long upTo, long written)
    {
        long from = Math.Max(Math.Max(_readerTrimmedThrough, written - SlotCount),
                             upTo - (long)Math.Max(1, FrameRate) * ResidentSeconds);

        if (upTo > from)
        {
            long first = from % SlotCount;
            long count = upTo - from;
            long toEnd = Math.Min(count, SlotCount - first);

            VirtualUnlock(_base + FileHeaderBytes + first * _stride, (nuint)(toEnd * _stride));

            if (count > toEnd)
                VirtualUnlock(_base + FileHeaderBytes, (nuint)((count - toEnd) * _stride));
        }

        _readerTrimmedThrough = Math.Max(_readerTrimmedThrough, upTo);
    }

    /// <summary>
    /// Writes the ring to its file a moment behind the writer, so its pages are clean.
    ///
    /// Left to Windows, the dirty pages of a mapped file wait in the modified list until the
    /// mapped page writer gets round to them, and on a machine with plenty of memory that is a
    /// long wait: 28 GB of the ring was sitting there, counted as memory in use, while it was
    /// measured on air. Written straight away they move to the standby cache, which Windows
    /// counts as available and gives up the moment anything else wants it.
    ///
    /// On its own thread, below normal priority: a flush waits for the disk, and the writer has
    /// only a few seconds of slack. Half a second behind the writer, so the slot being written
    /// is never the one being flushed, and a second at a time so a dispose is never kept waiting.
    /// </summary>
    private unsafe void FlushLoop()
    {
        int rate = Math.Max(1, FrameRate);
        long lag = Math.Max(1, rate / 2);
        bool said = false;

        while (!_stopFlushing.Wait(FlushEveryMs))
        {
            // Direct writes are on disk when they return: nothing to flush, only disk to give back.
            if (_direct)
            {
                ReleaseBehind();
                continue;
            }

            if (_ramSlots > 0 && _overflow is null) SpillAhead();

            while (!_stopFlushing.IsSet && Volatile.Read(ref _disposed) == 0)
            {
                long written = Volatile.Read(ref _written);
                long upTo = written - lag;

                // Never further back than the ring still holds: older frames have been written over.
                long from = Math.Max(Volatile.Read(ref _flushedThrough), written - SlotCount + lag);
                if (upTo <= from) break;

                long count = Math.Min(upTo - from, FlushChunkFrames);
                long first = from % SlotCount;
                long toEnd = Math.Min(count, SlotCount - first);

                long started = System.Diagnostics.Stopwatch.GetTimestamp();

                bool ok = FlushViewOfFile(_base + FileHeaderBytes + first * _stride, (nuint)(toEnd * _stride));
                if (count > toEnd)
                    ok &= FlushViewOfFile(_base + FileHeaderBytes, (nuint)((count - toEnd) * _stride));

                NoteSlowest(ref _slowestFlush, started);

                // Not fatal: Windows writes the pages itself in the end. Worth a line once,
                // because until it does they stay in memory.
                if (!ok && !said)
                {
                    said = true;
                    ActivityLog.Shared.Warn(LogSource.TidalLock,
                        $"The delay ring could not be written ahead to disk (error {Marshal.GetLastWin32Error()}); " +
                        "Windows will write it in its own time, using more memory meanwhile.",
                        @event: "delay.flushfailed", file: Path);
                }

                Volatile.Write(ref _flushedThrough, from + count);
            }

            ReleaseBehind();
        }
    }

    /// <summary>
    /// The oldest frame anything may still want, never past a frame not yet on disk. See <see cref="KeepFloor"/>.
    /// </summary>
    private long ReleaseFloor(long written) => Math.Min(KeepFloor(written), Volatile.Read(ref _flushedThrough));

    /// <summary>
    /// The oldest frame anything may still want: the kept distance behind the writer, less a
    /// margin, and never past a transmitter's hold.
    /// </summary>
    private long KeepFloor(long written)
    {
        long margin = (long)Math.Max(1, FrameRate) * ReaderMarginSeconds;
        long keep = TargetFrames;

        try
        {
            if (Volatile.Read(ref _keep) is { } asked) keep = Math.Max(0, asked());
        }
        catch
        {
            keep = SlotCount;                  // when in doubt, keep everything
        }

        long floor = written - keep - margin;

        lock (_holdsLock)
            foreach (DelayHold hold in _holds)
                floor = Math.Min(floor, hold.At - margin);

        return floor;
    }

    /// <summary>
    /// Gives the disk space of frames nothing will read again back to the volume, so the ring
    /// file takes what it holds and not its whole length. On the flusher's thread, after a flush.
    ///
    /// <b>Only when the volume is short of room.</b> Giving space back is not free: each chunk is a
    /// TRIM on an SSD, and the writer then lands new frames in holes the file system has to fill
    /// again. A ready line did that nonstop for no gain on a drive with hundreds of gigabytes free.
    /// With it off, frames lost on this plant fell from 17,323 in 22 minutes to 682 in 46 - but did
    /// not stop, and the queue was made longer in the same change, so how much was this is not known.
    ///
    /// The mark moves before the zeroing, so a reader is turned away from a frame before it can
    /// be cleared under it. Never within a few seconds of the writer's next slots: zeroing a
    /// slot the writer had just filled again would put a black frame in the delay.
    /// </summary>
    private void ReleaseBehind()
    {
        if (!_sparse || Volatile.Read(ref _releaseBroken) != 0) return;
        if (!AlwaysRelease && VolumeHasRoom()) return;

        long wrapMargin = (long)Math.Max(1, FrameRate) * WrapMarginSeconds;

        while (!_stopFlushing.IsSet && Volatile.Read(ref _disposed) == 0)
        {
            long written = Volatile.Read(ref _written);
            long floor = ReleaseFloor(written);
            long from = Math.Max(Volatile.Read(ref _releasedThrough), written - SlotCount + wrapMargin);
            if (floor <= from) return;

            long count = Math.Min(floor - from, ReleaseChunkFrames);
            Volatile.Write(ref _releasedThrough, from + count);

            long first = from % SlotCount;
            long toEnd = Math.Min(count, SlotCount - first);

            long started = System.Diagnostics.Stopwatch.GetTimestamp();

            bool ok = ZeroSlots(first, toEnd);
            if (count > toEnd) ok &= ZeroSlots(0, count - toEnd);

            NoteSlowest(ref _slowestRelease, started);

            if (!ok)
            {
                // Not fatal: the ring just keeps the disk it has, as it always used to.
                Volatile.Write(ref _releaseBroken, 1);
                ActivityLog.Shared.Warn(LogSource.TidalLock,
                    $"The delay ring could not give disk space back (error {Marshal.GetLastWin32Error()}); " +
                    "it will take its full size on disk.",
                    @event: "delay.releasefailed", file: Path);
                return;
            }
        }
    }

    private int _releaseBroken;

    /// <summary>
    /// Gives disk back whatever the volume has free, as the line always used to. For tests,
    /// whose rings are far too small for the volume ever to be short of room.
    /// </summary>
    internal bool AlwaysRelease
    {
        get => Volatile.Read(ref _alwaysRelease);
        set => Volatile.Write(ref _alwaysRelease, value);
    }

    private bool _alwaysRelease;
    private long _freeCheckedAt;
    private bool _freeChecked;
    private bool _volumeHasRoom;

    /// <summary>
    /// True while the ring's volume could take the ring's whole length and the margin besides,
    /// so nothing need be given back. The flusher's thread only; asked of the volume every ten
    /// seconds at most. When the volume cannot be asked, false: give back, as before.
    /// </summary>
    private bool VolumeHasRoom()
    {
        long now = Environment.TickCount64;
        if (_freeChecked && now - _freeCheckedAt < FreeCheckEveryMs) return _volumeHasRoom;
        _freeChecked = true;
        _freeCheckedAt = now;

        try
        {
            string? root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(Path));
            long free = root is { Length: > 0 } ? new DriveInfo(root).AvailableFreeSpace : 0;
            long length = FileHeaderBytes + SlotCount * _stride;

            _volumeHasRoom = free >= length + ReleaseFreeMarginBytes;
        }
        catch
        {
            _volumeHasRoom = false;
        }

        return _volumeHasRoom;
    }

    private unsafe bool ZeroSlots(long firstSlot, long count)
    {
        long* range = stackalloc long[2];
        range[0] = FileHeaderBytes + firstSlot * _stride;
        range[1] = range[0] + count * _stride;

        return DeviceIoControl(_file.SafeFileHandle, SetZeroDataCode, range, 16, null, 0, out _, IntPtr.Zero);
    }

    internal static unsafe bool SetSparse(FileStream file)
    {
        try
        {
            return DeviceIoControl(file.SafeFileHandle, SetSparseCode, null, 0, null, 0, out _, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Hands a frame to the line from the capture loop. Returns immediately: the copy into
    /// the mapped view happens on the line's own thread.
    ///
    /// This is the whole reason the writer thread exists. Copying four megabytes into a
    /// mapped view takes a soft page fault per page on first touch, and once the ring wraps
    /// it contends with the operating system writing those pages back. The receiver has four
    /// slots of headroom; a single write-back stall would cost frames off the wire. So the
    /// slot loop does one bounded, non-blocking hand-off and nothing else — and if the line
    /// cannot keep up, the delay loses a frame rather than the recording doing so.
    /// </summary>
    public void Offer(CapturedFrame frame)
    {
        // Inside the try: a line let go of altogether has no queue to ask, and a throw from here
        // would stop the frame reaching the lines subscribed after this one.
        try
        {
            if (_queue is null || _queue.IsAddingCompleted) return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        // The queue's claim on the picture, taken before the writer can reach it and given back
        // on every way out of here that did not hand the frame over.
        frame.Retain();

        try
        {
            if (_queue.TryAdd(frame))
            {
                Interlocked.Increment(ref _accepted);

                int waiting = _queue.Count;
                if (waiting > Volatile.Read(ref _queueHigh)) Volatile.Write(ref _queueHigh, waiting);
                return;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            frame.Release();
            return;                       // sealed between the check and the add
        }

        frame.Release();

        long dropped = Interlocked.Increment(ref _dropped);

        // A frame that never reached the ring is a frame that will never reach air, which is
        // worth a line of its own the moment it happens — rate-limited, because a disk that
        // cannot keep up drops them by the hundred.
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _dropSaidAt) > 5000)
        {
            Volatile.Write(ref _dropSaidAt, now);

            // What was slowest since the last of these lines, so the line says which wait it
            // was rather than only that the disk was behind. Read and reset together.
            long write = TakeSlowestMs(ref _slowestWrite);
            long flush = TakeSlowestMs(ref _slowestFlush);
            long release = TakeSlowestMs(ref _slowestRelease);
            int queued = Interlocked.Exchange(ref _queueHigh, 0);
            int depth = _queue.BoundedCapacity;

            ActivityLog.Shared.Warn(LogSource.TidalLock,
                $"DROP  frame {frame.Sequence} not taken into the delay ring - disk {Disk} could not " +
                $"keep up ({dropped} dropped so far). It will not reach air. Slowest since last: " +
                $"write {write} ms, flush {flush} ms, give-back {release} ms; queue {queued}/{depth}.",
                @event: "delay.dropped",
                file: Path,
                detail: $"frame {frame.Sequence}\ndropped total {dropped}\ndisk {Disk}\nring {Path}\n" +
                        $"slowest write {write} ms\nslowest flush {flush} ms\nslowest give-back {release} ms\n" +
                        $"queue high {queued}/{depth}\nsparse {_sparse}");
        }
    }

    // Slowest of each since the last drop line, in stopwatch ticks, and the deepest the queue got.
    private long _slowestWrite;
    private long _slowestFlush;
    private long _slowestRelease;
    private int _queueHigh;

    /// <summary>Keeps the longest time since <paramref name="started"/>. Each figure has one thread writing it.</summary>
    private static void NoteSlowest(ref long slowest, long started)
    {
        long took = System.Diagnostics.Stopwatch.GetTimestamp() - started;
        if (took > Volatile.Read(ref slowest)) Volatile.Write(ref slowest, took);
    }

    private static long TakeSlowestMs(ref long slowest) =>
        Interlocked.Exchange(ref slowest, 0) * 1000 / System.Diagnostics.Stopwatch.Frequency;

    private void WriteLoop()
    {
        try
        {
            foreach (CapturedFrame frame in _queue!.GetConsumingEnumerable())
            {
                // Copied into the ring, and the buffer is then free for a later frame.
                long started = System.Diagnostics.Stopwatch.GetTimestamp();

                try
                {
                    Write(frame.Video, frame.VideoBytes, frame.Audio, frame.AudioBytes, frame.AudioReal,
                          frame.Sequence, frame.AudioChannels);
                }
                finally
                {
                    frame.Release();
                    NoteSlowest(ref _slowestWrite, started);
                }
            }
        }
        catch (Exception ex)
        {
            Fault ??= ex.Message;
        }
    }

    /// <summary>
    /// No more frames are coming. The drain runs to here and then stops.
    ///
    /// The end is the number of frames <i>accepted</i>, not the number written so far: some
    /// are still in the queue on their way to the ring, and telling the transmitter to stop
    /// short of them would clip the end of the recording off the transmission.
    /// </summary>
    public void Seal()
    {
        try { _queue?.CompleteAdding(); } catch (ObjectDisposedException) { }

        long end = Math.Max(Volatile.Read(ref _written), Interlocked.Read(ref _accepted));
        Interlocked.CompareExchange(ref _sealedAt, end, -1);
    }

    /// <summary>A frame the capture loop offered that the ring could not take.</summary>
    public void CountDrop() => Interlocked.Increment(ref _dropped);

    // ------------------------------------------------------------------ memory ring

    // Physical memory left alone when the ring in RAM is sized: Windows, the recorder's encoders,
    // Live Edit's players, inserts and exports, and everything else on the machine live in this.
    // At 4 GB a second ring could leave the machine paging the moment an export started.
    private const long MemoryReserveBytes = 6L * 1024 * 1024 * 1024;

    /// <summary>The reserve in force. Tests lower it, so they do not depend on the machine's free memory.</summary>
    internal static long MemoryReserve { get; set; } = MemoryReserveBytes;

    // The line holding the process's one ring in RAM, if any. One, because a second ring of the
    // same recording doubles the memory for nothing - and could only ever get the scraps the
    // first left, a fraction of its delay, so the disk would be on air again anyway. Under
    // WorkingSetGate.
    private static DelayLine? _memoryHolder;

    /// <summary>
    /// Raised once the line is closed and its RAM given back - after its last holder let go, which
    /// for a sealed line is when the transmitter has played out what was in it. From whichever
    /// thread let go last.
    /// </summary>
    public event Action<DelayLine>? Closed;

    /// <summary>True while some line in this process holds its delay in RAM.</summary>
    public static bool MemoryRingHeld
    {
        get { lock (WorkingSetGate) return _memoryHolder is not null; }
    }

    // How far ahead of a frame leaving memory the flusher writes it out, if it is still wanted,
    // so the writer itself almost never has to.
    private const int SpillLeadSeconds = 4;

    private unsafe byte* _ram;
    private long _ramSlots;
    private int _ramLocked;
    private Task? _ramLocking;
    private long _ramWorkingSetAdded;
    private readonly IDisposable _ramGauge;

    // Frames before _spilledThrough have been decided: written to the file if still wanted, or
    // let go. Frames before _lostThrough were let go, and once out of memory cannot be read.
    private long _spilledThrough;
    private long _lostThrough;
    private long _spilled;
    private readonly object _spillLock = new();
    private byte[]? _spillStaging;
    private int _spillSaid;

    private static readonly object WorkingSetGate = new();

    private unsafe byte* RamSlotAt(long sequence) => _ram + sequence % _ramSlots * _stride;

    /// <summary>
    /// Takes the ring in RAM: as many of <paramref name="slots"/> as physical memory can spare
    /// beyond a reserve, or none, and the line runs from disk as it always did. Then locks it in
    /// the background, so a line opened on the interface's thread does not wait for 13 GB to be
    /// made resident.
    /// </summary>
    private unsafe void OpenMemoryRing(long slots, long required)
    {
        if (slots <= 0) return;

        // Claimed before anything is allocated, so two lines opening at once cannot both take it.
        string? holder = null;

        lock (WorkingSetGate)
        {
            if (_memoryHolder is null) _memoryHolder = this;
            else holder = _memoryHolder.Path;
        }

        if (holder is not null)
        {
            ActivityLog.Shared.Warn(LogSource.TidalLock,
                $"DELAY  another delay line already holds the RAM ({System.IO.Path.GetFileName(holder)}), so this one cannot.",
                @event: "delay.memoryshort", file: Path);
            return;
        }

        try
        {
            AllocateMemoryRing(slots, required);
        }
        finally
        {
            if (_ram is null)
                lock (WorkingSetGate)
                    if (_memoryHolder == this) _memoryHolder = null;
        }
    }

    private unsafe void AllocateMemoryRing(long slots, long required)
    {
        int rate = Math.Max(1, FrameRate);
        long wanted = slots;
        long room = Math.Max(0, AvailablePhysical() - MemoryReserve);
        if (slots * _stride > room) slots = room / _stride;

        // Short of the delay and its guard it is worse than none: a transmitter a delay behind would
        // have every frame written out of memory and read back off the disk.
        if (slots < Math.Max(required, 1))
        {
            ActivityLog.Shared.Warn(LogSource.TidalLock,
                $"DELAY  not enough free memory to hold the delay in RAM: {Describe(room)} to spare after leaving " +
                $"{Describe(MemoryReserve)} for everything else, and {Describe(Math.Max(required, 1) * _stride)} needed.",
                @event: "delay.memoryshort", file: Path);
            return;
        }

        void* ram = VirtualAlloc(null, (nuint)(slots * _stride), MemCommit | MemReserve, PageReadWrite);

        if (ram is null)
        {
            ActivityLog.Shared.Warn(LogSource.TidalLock,
                $"DELAY  {Describe(slots * _stride)} for the delay in RAM could not be had (error " +
                $"{Marshal.GetLastWin32Error()}).",
                @event: "delay.memoryshort", file: Path);
            return;
        }

        _ram = (byte*)ram;
        _ramSlots = slots;

        TimeSpan held = TimeSpan.FromSeconds(slots / (double)rate);

        // Less than asked for is usual: what was asked for includes all the insert room there might be.
        ActivityLog.Shared.Info(LogSource.TidalLock,
            $"DELAY  the newest {Describe(held)} of the ring is held in RAM ({Describe(slots * _stride)})" +
            (slots < wanted
                ? $", of the {Describe(TimeSpan.FromSeconds(wanted / (double)rate))} asked for - all that memory could spare"
                : "") +
            $". {Describe(Math.Max(0, AvailablePhysical()))} of memory was free.",
            @event: "delay.memory", file: Path);

        _ramLocking = Task.Run(LockMemoryRing);
    }

    /// <summary>
    /// Locks the ring in RAM, so Windows cannot page it out - a frame read back from the page file
    /// would be the disk on the path to air again, on the system drive. Locking that much needs
    /// the process's minimum working set raised by as much first. Best effort: unlocked, the ring
    /// still works, and with memory to spare Windows leaves it where it is.
    /// </summary>
    private unsafe void LockMemoryRing()
    {
        long bytes = _ramSlots * _stride;

        try
        {
            lock (WorkingSetGate)
            {
                IntPtr me = GetCurrentProcess();

                if (!GetProcessWorkingSetSize(me, out nuint min, out nuint max) ||
                    !SetProcessWorkingSetSizeEx(me, min + (nuint)bytes, max + (nuint)bytes,
                                                QuotaLimitsHardWsMinDisable | QuotaLimitsHardWsMaxDisable))
                {
                    SayNotLocked(Marshal.GetLastWin32Error());
                    return;
                }

                _ramWorkingSetAdded = bytes;
            }

            if (Volatile.Read(ref _disposed) != 0) return;

            if (!VirtualLock(_ram, (nuint)bytes))
            {
                SayNotLocked(Marshal.GetLastWin32Error());
                return;
            }

            Volatile.Write(ref _ramLocked, 1);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            SayNotLocked(0);
        }
    }

    private void SayNotLocked(int error) =>
        ActivityLog.Shared.Info(LogSource.TidalLock,
            $"DELAY  the delay's RAM could not be locked (error {error}); it is held all the same, " +
            "and only paged out if the machine runs short of memory.",
            @event: "delay.memoryunlocked", file: Path);

    /// <summary>Gives the ring's RAM back, and the working set it was locked under. From Dispose, writer stopped.</summary>
    private unsafe void CloseMemoryRing()
    {
        if (_ram is null) return;

        try { _ramLocking?.Wait(TimeSpan.FromSeconds(30)); } catch { }

        VirtualFree(_ram, 0, MemRelease);       // a lock goes with the memory
        _ram = null;

        lock (WorkingSetGate)
            if (_memoryHolder == this) _memoryHolder = null;

        if (_ramWorkingSetAdded <= 0) return;

        lock (WorkingSetGate)
        {
            IntPtr me = GetCurrentProcess();

            if (GetProcessWorkingSetSize(me, out nuint min, out nuint max))
            {
                nuint less = (nuint)_ramWorkingSetAdded;
                SetProcessWorkingSetSizeEx(me, min > less ? min - less : min, max > less ? max - less : max,
                                           QuotaLimitsHardWsMinDisable | QuotaLimitsHardWsMaxDisable);
            }

            _ramWorkingSetAdded = 0;
        }
    }

    /// <summary>
    /// Decides every frame before <paramref name="upTo"/> that has not been: written to the file
    /// if something still wants it once it leaves memory - a transmitter's hold, or the distance the
    /// line keeps - and let go otherwise. One at a time under the lock, so the writer waiting on a
    /// frame it is about to write over never waits behind more than one.
    /// </summary>
    private void SpillThrough(long upTo)
    {
        while (true)
        {
            lock (_spillLock)
            {
                long seq = _spilledThrough;
                if (seq >= upTo) return;

                // With the whole ring in memory there is nothing to spill to: by the time a frame leaves
                // memory the ring has come round on it, and no read of it would be answered.
                if (_ramSlots < SlotCount && seq >= KeepFloor(Volatile.Read(ref _written)))
                {
                    // Reached by the writer before the overflow's thread: it has fallen behind, and the
                    // recorder is not made to wait for it.
                    if (_overflow is { } overflow) overflow.Lose(seq);
                    else SpillOne(seq);
                }
                else
                {
                    Volatile.Write(ref _lostThrough, seq + 1);
                }

                Volatile.Write(ref _spilledThrough, seq + 1);
            }
        }
    }

    /// <summary>The flusher's share: frames about to leave memory, written out before the writer gets to them.</summary>
    private void SpillAhead()
    {
        long upTo = Volatile.Read(ref _written) - _ramSlots + (long)Math.Max(1, FrameRate) * SpillLeadSeconds;

        while (!_stopFlushing.IsSet && Volatile.Read(ref _disposed) == 0 &&
               Volatile.Read(ref _spilledThrough) < upTo)
            SpillThrough(Volatile.Read(ref _spilledThrough) + 1);
    }

    /// <summary>
    /// One frame from memory to its slot of the file, the way the writer writes a slot: the
    /// sequence marked -1 in what is written and set through the mapping after, so a reader of an
    /// older frame in that slot discards what it copied. Under the spill lock.
    /// </summary>
    private unsafe void SpillOne(long seq)
    {
        byte* ram = RamSlotAt(seq);
        if (Volatile.Read(ref *(long*)(ram + OffSequence)) != seq) return;

        byte* disk = SlotAt(seq);
        byte[] staging = _spillStaging ??= GC.AllocateUninitializedArray<byte>((int)_stride);

        fixed (byte* s = staging)
        {
            Buffer.MemoryCopy(ram, s, _stride, _stride);
            *(long*)(s + OffSequence) = -1L;

            try
            {
                if (Volatile.Read(ref _fileWritesBroken) != 0) throw new IOException("file writes refused earlier");
                RandomAccess.Write(_file.SafeFileHandle, staging, FileHeaderBytes + seq % SlotCount * _stride);
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _fileWritesBroken, 1) == 0)
                    ActivityLog.Shared.Warn(LogSource.TidalLock,
                        $"The delay ring at {Path} could not be written as a file ({ex.Message}); writing it through " +
                        "memory instead.", @event: "delay.filewritefailed", file: Path);

                Volatile.Write(ref *(long*)(disk + OffSequence), -1L);
                Buffer.MemoryCopy(s + OffSequence + 8, disk + OffSequence + 8, _stride - 8, _stride - 8);
            }
        }

        Volatile.Write(ref *(long*)(disk + OffSequence), seq);
        Interlocked.Increment(ref _spilled);

        if (Interlocked.Exchange(ref _spillSaid, 1) == 0)
            ActivityLog.Shared.Info(LogSource.TidalLock,
                $"DELAY  frames are leaving RAM still held - the delay has grown past the " +
                $"{Describe(TimeSpan.FromSeconds(_ramSlots / (double)Math.Max(1, FrameRate)))} held in memory - " +
                $"so they are written to disk {Disk} and read back from there.",
                @event: "delay.spilling", file: Path);
    }

    internal static unsafe long AvailablePhysical()
    {
        var status = new MemoryStatusEx { Length = (uint)sizeof(MemoryStatusEx) };
        return GlobalMemoryStatusEx(ref status) ? (long)status.AvailPhys : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint QuotaLimitsHardWsMinDisable = 0x2;
    private const uint QuotaLimitsHardWsMaxDisable = 0x8;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe void* VirtualAlloc(void* address, nuint size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool VirtualFree(void* address, nuint size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool VirtualLock(void* address, nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessWorkingSetSize(IntPtr process, out nuint min, out nuint max);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(IntPtr process, nuint min, nuint max, uint flags);

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// Reads one frame, or says why it could not. Called only by the transmitter's thread.
    ///
    /// On anything but <see cref="DelayReadResult.Ok"/> the buffer is left exactly as it was,
    /// so the caller can still fall back on whatever it last held.
    /// </summary>
    public unsafe DelayReadResult TryRead(long sequence, DelayFrameBuffer into)
    {
        if (Volatile.Read(ref _disposed) != 0) return DelayReadResult.EndOfLine;

        long sealedAt = Volatile.Read(ref _sealedAt);
        if (sealedAt >= 0 && sequence >= sealedAt) return DelayReadResult.EndOfLine;

        long written = Volatile.Read(ref _written);
        if (sequence >= written) return DelayReadResult.NotYetWritten;
        if (sequence < written - SlotCount) return DelayReadResult.Lapped;

        if (_direct) return TryReadDirect(sequence, into);

        bool inMemory = _ram is not null && sequence >= written - _ramSlots;
        if (!inMemory && sequence < Volatile.Read(ref _releasedThrough)) return DelayReadResult.Lapped;

        NoteReader(written - sequence);

        if (inMemory)
        {
            if (CopySlot(RamSlotAt(sequence), sequence, into) == DelayReadResult.Ok) return DelayReadResult.Ok;

            // It left memory while being read. The writer decides a frame before writing over it,
            // so if anything wanted it, it is in the file by now.
            if (sequence < Volatile.Read(ref _releasedThrough)) return DelayReadResult.Lapped;
        }

        if (_overflow is { } overflow)
            return sequence < OldestHeld ? DelayReadResult.Lapped : overflow.TryRead(sequence, into);

        return CopySlot(SlotAt(sequence), sequence, into);
    }

    /// <summary>Direct: from the read-ahead when it has the frame, otherwise straight off the disk.</summary>
    private unsafe DelayReadResult TryReadDirect(long sequence, DelayFrameBuffer into)
    {
        if (sequence < Volatile.Read(ref _releasedThrough)) return DelayReadResult.Lapped;

        if (Volatile.Read(ref _readAhead) is { } ahead && ahead.TryCopy(sequence, into)) return DelayReadResult.Ok;

        lock (_directReadLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || _directRead is null) return DelayReadResult.EndOfLine;
            if (!ReadDirect(sequence, _directRead, (int)_stride)) return DelayReadResult.Lapped;

            return CopySlot(_directRead, sequence, into);
        }
    }

    /// <summary>
    /// Reads frame <paramref name="sequence"/>'s slot - the first <paramref name="bytes"/> of it - off
    /// the disk into <paramref name="buffer"/>, sector-aligned memory. True only for a whole, good copy.
    ///
    /// <b>Torn reads.</b> The writer starts writing over a slot when the count of frames written
    /// reaches that frame plus the ring's length, and counts the frame it wrote only once the write
    /// has returned. So a read is good only when that count is short of the frame plus the ring's
    /// length both before and after it: then nothing was writing the slot while it was read. The
    /// sequence in the copy's header then has to be the one asked for, and marked written - a slot
    /// never written, or given back to the disk, reads as zeros.
    /// </summary>
    private unsafe bool ReadDirect(long sequence, byte* buffer, int bytes)
    {
        long written = Volatile.Read(ref _written);
        if (sequence >= written || written - sequence >= SlotCount) return false;
        if (sequence < Volatile.Read(ref _releasedThrough)) return false;

        int got = RandomAccess.Read(_file.SafeFileHandle, new Span<byte>(buffer, bytes),
                                    FileHeaderBytes + sequence % SlotCount * _stride);

        if (got < bytes) return false;

        Interlocked.MemoryBarrier();

        if (Volatile.Read(ref _written) - sequence >= SlotCount) return false;
        if (sequence < Volatile.Read(ref _releasedThrough)) return false;

        return *(long*)(buffer + OffSequence) == sequence && (*(int*)(buffer + OffFlags) & FlagWritten) != 0;
    }

    /// <summary>How far ahead of a transmitter a direct line reads into memory of its own.</summary>
    private const int ReadAheadSeconds = 3;

    /// <summary>
    /// A direct line's read-ahead: the next few seconds after where the transmitter is, read off the
    /// disk on a thread of its own into memory the line owns, so the transmitter's reads are memory
    /// copies and a moment's wait on the disk is absorbed here rather than seen on air. About 300 MB
    /// at 1080p25, taken only once a transmitter reads.
    ///
    /// A buffer's frame is published only once whole, and unpublished before it is read over, so a
    /// copy out of it is checked against that before and after (see <see cref="TryCopy"/>).
    /// </summary>
    private sealed unsafe class ReadAhead : IDisposable
    {
        private readonly DelayLine _line;
        private readonly int _count;
        private readonly byte* _buffers;
        private readonly long[] _held;
        private readonly AutoResetEvent _wake = new(false);
        private readonly Thread _thread;
        private long _from = -1;
        private volatile bool _stop;

        public ReadAhead(DelayLine line, int count)
        {
            _line = line;
            _count = count;
            _buffers = (byte*)NativeMemory.AlignedAlloc((nuint)(count * line._stride), DirectAlignment);
            _held = Enumerable.Repeat(-1L, count).ToArray();

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "tidal delay read-ahead",
                Priority = ThreadPriority.AboveNormal,
            };

            _thread.Start();
        }

        public long Bytes => (long)_count * _line._stride;

        /// <summary>The transmitter is at <paramref name="sequence"/>: keep the frames from there on in memory.</summary>
        public void From(long sequence)
        {
            Volatile.Write(ref _from, sequence);
            _wake.Set();
        }

        public bool TryCopy(long sequence, DelayFrameBuffer into)
        {
            int i = (int)(sequence % _count);
            if (Volatile.Read(ref _held[i]) != sequence) return false;

            DelayReadResult copied = _line.CopySlot(_buffers + i * _line._stride, sequence, into);

            // Unpublished before any read over it begins: still published now means the copy was whole.
            Interlocked.MemoryBarrier();
            return copied == DelayReadResult.Ok && Volatile.Read(ref _held[i]) == sequence;
        }

        private void Loop()
        {
            while (!_stop)
            {
                _wake.WaitOne(40);

                long from = Volatile.Read(ref _from);
                if (from < 0) continue;

                for (long s = from; s < from + _count && !_stop; s++)
                {
                    // The transmitter has moved on: start again from where it is now.
                    if (Volatile.Read(ref _from) != from) break;
                    if (s >= _line.FramesWritten) break;

                    int i = (int)(s % _count);
                    if (Volatile.Read(ref _held[i]) == s) continue;

                    Volatile.Write(ref _held[i], -1);

                    try
                    {
                        if (_line.ReadDirect(s, _buffers + i * _line._stride, (int)_line._stride))
                            Volatile.Write(ref _held[i], s);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
                    {
                        // The transmitter's own read reports it, and this tries again on the next pass.
                    }
                }
            }
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(5));
            NativeMemory.AlignedFree(_buffers);
            _wake.Dispose();
        }
    }

    /// <summary>
    /// Copies frame <paramref name="sequence"/> out of a slot, in memory or in the file, checking
    /// the slot's sequence before and after so a frame written over mid-copy is never handed on.
    /// </summary>
    internal unsafe DelayReadResult CopySlot(byte* slot, long sequence, DelayFrameBuffer into)
    {
        if (Volatile.Read(ref *(long*)(slot + OffSequence)) != sequence) return DelayReadResult.Lapped;

        int flags = *(int*)(slot + OffFlags);
        if ((flags & FlagWritten) == 0) return DelayReadResult.Lapped;

        int videoBytes = Math.Min(*(int*)(slot + OffVideoBytes), FrameBytes);
        int audioBytes = Math.Min(*(int*)(slot + OffAudioBytes), _audioCapacity);
        bool audioReal = (flags & FlagAudioReal) != 0;
        long recordingFrame = *(long*)(slot + OffRecordingFrame);

        fixed (byte* dst = into.Video)
            Buffer.MemoryCopy(slot + SlotHeaderBytes, dst, into.Video.Length, videoBytes);

        if (audioBytes > 0)
        {
            fixed (byte* dst = into.Audio)
                Buffer.MemoryCopy(slot + SlotHeaderBytes + FrameBytes, dst, into.Audio.Length, audioBytes);
        }

        // A full fence, not an acquire. Volatile.Read only stops later loads from being
        // hoisted above it; it does nothing to stop the copy above from sinking below it,
        // which would let the check pass on a frame that was still being copied as the
        // writer overwrote it. Release builds do exactly that, and the torn-frame test
        // catches it. The barrier pins the copy in front of the check.
        Interlocked.MemoryBarrier();

        // Read the sequence again. If the writer came all the way round while this was
        // copying, the buffer now holds half of two different frames - and it is deliberately
        // not the buffer the caller is still holding on to.
        if (Volatile.Read(ref *(long*)(slot + OffSequence)) != sequence) return DelayReadResult.Lapped;

        into.VideoBytes = videoBytes;
        into.AudioBytes = audioBytes;
        into.AudioChannels = ChannelsIn(flags);
        into.AudioWasReal = audioReal;
        into.Sequence = sequence;
        into.RecordingFrame = recordingFrame;
        return DelayReadResult.Ok;
    }

    /// <summary>
    /// Remembers how far behind the farthest reader is, so its pages are not trimmed from in front
    /// of it. Several readers share a line at different distances: a nearer one only takes over
    /// once the farther has gone quiet. No lock, on the transmitter's path; two readers racing here
    /// can only leave a figure one read out of date, and the margin covers that.
    /// </summary>
    private void NoteReader(long distance)
    {
        long now = Environment.TickCount64;

        if (distance < Volatile.Read(ref _readerDistance) &&
            now - Volatile.Read(ref _readerSeenAt) <= ReaderLapseMs) return;

        Volatile.Write(ref _readerDistance, distance);
        Volatile.Write(ref _readerSeenAt, now);
    }

    // ------------------------------------------------------------------ reading ahead

    /// <summary>How far ahead of a transmitter its frames are brought back into memory.</summary>
    private const int PrefetchSeconds = 2;

    private long _prefetchedThrough = -1;
    private int _prefetching;

    /// <summary>
    /// Brings the next couple of seconds after <paramref name="sequence"/> back into memory, in
    /// the background, so the transmitter's reads find them there.
    ///
    /// <b>Why.</b> A transmitter a delay behind reads frames whose pages were taken out of the
    /// working set long ago, to keep the ring's memory down (see <see cref="TrimBehind"/>). Each
    /// read then faults its four or five megabytes back in, page by page - from the standby cache
    /// when it is lucky, and from the disk when anything else has wanted the memory meanwhile.
    /// The transmitter does that on its own thread, between pushes, and a read that waits on the
    /// disk longer than the card's queue lasts is a frame the card repeats on air and a frame the
    /// pacer skips afterwards to catch up: the flicker and the drops. Asked for here as one large
    /// read, ahead of time, the pages are resident before the transmitter gets to them.
    ///
    /// Never blocks the caller: at most one read-ahead is in flight, on the pool, and it is asked
    /// for again once the transmitter is a second into what was last brought in. Nothing trims the
    /// pages ahead of a reader; they go again a margin behind it, as before.
    /// </summary>
    public void PrefetchAhead(long sequence)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        // Direct: the line's own read-ahead, not the system's cache.
        if (_direct)
        {
            ReadAhead? reader = Volatile.Read(ref _readAhead);

            if (reader is null)
            {
                lock (_readAheadLock)
                {
                    if (Volatile.Read(ref _disposed) != 0) return;
                    reader = _readAhead ??= new ReadAhead(this, Math.Max(8, Math.Max(1, FrameRate) * ReadAheadSeconds));
                }
            }

            reader.From(sequence);
            return;
        }

        // Compressed, or about to be: decoded ahead from where the transmitter is.
        if (_overflow is { } overflow)
        {
            if (sequence < Volatile.Read(ref _written) - _ramSlots + (long)Math.Max(1, FrameRate) * SpillLeadSeconds)
                overflow.Want(sequence);
            return;
        }

        // In RAM: nothing to bring in.
        if (_ramSlots > 0 && sequence >= Volatile.Read(ref _written) - _ramSlots) return;

        long ahead = (long)Math.Max(1, FrameRate) * PrefetchSeconds;

        // Still well inside what the last read-ahead covered.
        if (sequence + ahead / 2 < Volatile.Read(ref _prefetchedThrough)) return;
        if (Interlocked.CompareExchange(ref _prefetching, 1, 0) != 0) return;

        long upTo = sequence + ahead;

        _ = Task.Run(() =>
        {
            try
            {
                Prefetch(sequence, upTo);
                Volatile.Write(ref _prefetchedThrough, upTo);
            }
            catch
            {
                // A hint, nothing more: the read itself still faults the pages in if this did not.
            }
            finally
            {
                Volatile.Write(ref _prefetching, 0);
            }
        });
    }

    private unsafe void Prefetch(long from, long upTo)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        long written = Volatile.Read(ref _written);

        from = Math.Max(Math.Max(from, written - SlotCount), Volatile.Read(ref _releasedThrough));
        upTo = Math.Min(upTo, _ramSlots > 0 ? written - _ramSlots : written);     // only what is on disk
        if (upTo <= from) return;

        long count = upTo - from;
        long first = from % SlotCount;
        long toEnd = Math.Min(count, SlotCount - first);

        MemoryRange* ranges = stackalloc MemoryRange[2];
        ranges[0] = new MemoryRange(_base + FileHeaderBytes + first * _stride, (nuint)(toEnd * _stride));

        int n = 1;
        if (count > toEnd)
            ranges[n++] = new MemoryRange(_base + FileHeaderBytes, (nuint)((count - toEnd) * _stride));

        PrefetchVirtualMemory(GetCurrentProcess(), (nuint)n, ranges, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly unsafe struct MemoryRange(void* address, nuint bytes)
    {
        public readonly void* Address = address;
        public readonly nuint Bytes = bytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool PrefetchVirtualMemory(IntPtr process, nuint count, MemoryRange* ranges, uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public DelayFrameBuffer NewBuffer() => new(FrameBytes, _audioCapacity);

    /// <summary>
    /// Which frame of the recording a slot holds, from its header alone - no copy of the picture.
    /// -1 when the slot is not there to ask: not written yet, lapped, or being written.
    /// </summary>
    public unsafe long RecordingFrameAt(long sequence)
    {
        if (Volatile.Read(ref _disposed) != 0 || sequence < 0) return -1;

        long written = Volatile.Read(ref _written);
        if (sequence >= written || sequence < written - SlotCount) return -1;

        // Direct: the slot's first sector off the disk, which holds its header.
        if (_direct)
        {
            lock (_directReadLock)
            {
                if (Volatile.Read(ref _disposed) != 0 || _directRead is null) return -1;
                return ReadDirect(sequence, _directRead, DirectAlignment)
                    ? *(long*)(_directRead + OffRecordingFrame)
                    : -1;
            }
        }

        if (_ram is not null && sequence >= written - _ramSlots)
        {
            long inMemory = RecordingFrameIn(RamSlotAt(sequence), sequence);
            if (inMemory >= 0) return inMemory;
        }

        if (_overflow is { } overflow) return overflow.RecordingFrameAt(sequence);

        if (sequence < Volatile.Read(ref _releasedThrough)) return -1;
        return RecordingFrameIn(SlotAt(sequence), sequence);
    }

    private static unsafe long RecordingFrameIn(byte* slot, long sequence)
    {
        if (Volatile.Read(ref *(long*)(slot + OffSequence)) != sequence) return -1;
        if ((*(int*)(slot + OffFlags) & FlagWritten) == 0) return -1;
        long recordingFrame = *(long*)(slot + OffRecordingFrame);

        Interlocked.MemoryBarrier();
        return Volatile.Read(ref *(long*)(slot + OffSequence)) == sequence ? recordingFrame : -1;
    }

    /// <summary>
    /// The slot holding frame <paramref name="recordingFrame"/> of the recording, or -1 if the
    /// line does not hold it - older than the ring reaches back, or not recorded yet.
    ///
    /// <b>Searched, not worked out.</b> A slot's recording frame is its sequence plus a constant
    /// only while nothing is dropped; a frame the line could not keep up with leaves a step in
    /// it. So it starts from the arithmetic and walks by the difference it finds, which lands in
    /// one or two looks, and settles for the nearest frame when the one asked for was dropped.
    /// </summary>
    public long SequenceOf(long recordingFrame)
    {
        long newest = FramesWritten - 1;
        long newestFrame = RecordingFrameAt(newest);
        if (newestFrame < 0 || recordingFrame > newestFrame) return -1;

        long seq = newest - (newestFrame - recordingFrame);

        for (int look = 0; look < 8; look++)
        {
            long at = RecordingFrameAt(seq);
            if (at < 0) return -1;
            if (at == recordingFrame) return seq;

            // The frame asked for was dropped, and this slot is beside the gap it left.
            long beside = RecordingFrameAt(at > recordingFrame ? seq - 1 : seq + 1);
            if (at > recordingFrame ? beside >= 0 && beside < recordingFrame : beside > recordingFrame)
                return seq;

            seq -= at - recordingFrame;
        }

        return -1;
    }

    // ------------------------------------------------------------------ lifetime

    /// <summary>
    /// The transmitter has to outlive the recorder by a whole delay, or the last minute of
    /// the programme never reaches air. So the line is shared and counted rather than owned
    /// by whichever half happens to stop first.
    /// </summary>
    public void AddRef() => Interlocked.Increment(ref _refs);

    /// <summary>
    /// A claim for a reader that found the line somewhere shared and so cannot know it is still
    /// open: false, and no claim taken, if its last holder has already let it go.
    /// </summary>
    public bool TryAddRef()
    {
        while (true)
        {
            int refs = Volatile.Read(ref _refs);
            if (refs <= 0 || Volatile.Read(ref _disposed) != 0) return false;
            if (Interlocked.CompareExchange(ref _refs, refs + 1, refs) == refs) return true;
        }
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) > 0) return;
        Dispose();
    }

    public unsafe void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _gauge.Dispose();
        _heldGauge.Dispose();
        _dirtyGauge.Dispose();
        _ramGauge.Dispose();

        // The flusher works on the mapping, so it stops first. A chunk is a second of frames;
        // this waits for the one in hand and no more.
        _stopFlushing.Set();
        if (_flusher.IsAlive) _flusher.Join(TimeSpan.FromSeconds(10));

        // Let the writer finish what it has in hand before the mapping goes away underneath it.
        try { _queue?.CompleteAdding(); } catch (ObjectDisposedException) { }
        if (_writer is { IsAlive: true }) _writer.Join(TimeSpan.FromSeconds(5));
        _queue?.Dispose();

        // Stopped first, because its thread can be waiting on the encoder; closed once that is out of it.
        _overflow?.Stop();
        _overflowThread?.Join(TimeSpan.FromSeconds(10));
        _overflow?.Dispose();

        CloseMemoryRing();

        if (_direct)
        {
            lock (_readAheadLock)
            {
                _readAhead?.Dispose();
                _readAhead = null;
            }

            // Under the read lock, so a read still in hand finishes before its buffer goes.
            lock (_directReadLock)
            {
                if (_directRead is not null) NativeMemory.AlignedFree(_directRead);
                if (_directWrite is not null) NativeMemory.AlignedFree(_directWrite);
                _directRead = null;
                _directWrite = null;
            }
        }

        if (_base is not null)
        {
            // Before the view: releasing them the other way round leaks the whole mapping.
            _view!.SafeMemoryMappedViewHandle.ReleasePointer();
            _base = null;
        }

        if (_view is not null)
        {
            // The view's handle closed by hand, because disposing the accessor first flushes all of
            // it: every frame in the ring written to disk, for a file deleted three lines down.
            // Measured at 37 s and 5.9 GB on the system disk for a ring of 4 GB, and a ready line is
            // ten times that.
            _view.SafeMemoryMappedViewHandle.Dispose();

            // Which then complains that the handle it wanted to flush is closed.
            try { _view.Dispose(); } catch (ObjectDisposedException) { }
        }

        _mmf?.Dispose();
        _file.Dispose();

        try { File.Delete(Path); } catch { /* the sweep will get it */ }

        try { Closed?.Invoke(this); }
        catch (Exception ex)
        {
            ActivityLog.Shared.Warn(LogSource.TidalLock, $"After the delay line closed: {ex.Message}",
                                    @event: "delay.closedfailed", file: Path);
        }
    }

    private static string Describe(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):F1} GB"
        : $"{bytes / (double)(1L << 20):F0} MB";

    private static string Describe(TimeSpan delay) =>
        delay.TotalMinutes >= 1 && delay.TotalSeconds % 60 == 0
            ? $"{delay.TotalMinutes:F0} min"
            : $"{delay.TotalSeconds:F0} s";

    // On a range that was never locked, this takes the pages out of the working set.
    [DllImport("kernel32.dll")]
    private static extern unsafe bool VirtualUnlock(void* address, nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool FlushViewOfFile(void* address, nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code,
        void* input, uint inputSize, void* output, uint outputSize, out uint returned, IntPtr overlapped);
}

/// <summary>
/// A transmitter's claim on a <see cref="DelayLine"/>'s frames from <see cref="At"/> on; see
/// <see cref="DelayLine.Hold"/>. Moved on as the transmitter reads, disposed when it stops.
/// </summary>
public sealed class DelayHold : IDisposable
{
    private readonly DelayLine _line;
    private long _at;
    private int _disposed;

    internal DelayHold(DelayLine line, long at)
    {
        _line = line;
        _at = at;
    }

    public long At => Volatile.Read(ref _at);

    public void Move(long sequence) => Volatile.Write(ref _at, sequence);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _line.Unhold(this);
    }
}
