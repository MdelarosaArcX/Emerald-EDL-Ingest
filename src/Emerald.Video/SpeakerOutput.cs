using System.Runtime.InteropServices;

namespace Emerald.Video;

/// <summary>
/// Plays a stereo pair out of the machine's own sound device, so an operator can hear what
/// is on the wire at the desk.
///
/// This is a <b>confidence monitor and nothing more</b>. It is not in the path to air, it is
/// not what gets recorded, and muting it changes neither. The SDI receiver and the sound card
/// run off different clocks and will drift apart over a long session, which is handled by
/// dropping a frame when the queue runs long rather than by resampling — a monitor that
/// occasionally skips 20 ms is fine, and a monitor that adds latency without bound is not.
///
/// Built on <c>waveOut</c> rather than a library: it is a handful of P/Invokes, it is present
/// on every Windows since forever, and Emerald has exactly one NuGet dependency and no reason
/// to acquire another for this.
/// </summary>
public sealed class SpeakerOutput : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int BytesPerSample = 2;

    /// <summary>
    /// Buffers in flight. Eight frames is about a third of a second — enough that a late
    /// slot does not gap the sound, short enough that what is heard is still what is on
    /// screen.
    /// </summary>
    private const int BufferCount = 8;

    /// <summary>
    /// Queue depth past which a frame is dropped instead of queued. The sound card being
    /// slower than the receiver would otherwise grow the delay without limit until the
    /// monitor was seconds behind the picture.
    /// </summary>
    private const int MaxQueued = 6;

    private const uint WhdrDone = 0x00000001;
    private const uint WhdrPrepared = 0x00000002;

    private const int WaveFormatPcm = 1;
    private const int WaveMapper = -1;

    // WAVEHDR on x64: lpData, dwBufferLength, dwBytesRecorded, dwUser, dwFlags, dwLoops,
    // lpNext, reserved. Hand-built at offsets for the same reason VHD_AUDIOINFO is.
    private const int HeaderBytes = 48;
    private const int HdrData = 0, HdrLength = 8, HdrFlags = 24;

    private readonly object _gate = new();

    private IntPtr _device;
    private IntPtr[] _headers = Array.Empty<IntPtr>();
    private IntPtr[] _buffers = Array.Empty<IntPtr>();
    private int _bufferBytes;
    private int _next;
    private bool _disposed;

    /// <summary>Frames dropped because the sound card fell behind. Diagnostic only.</summary>
    public long Dropped { get; private set; }

    /// <summary>Whether the device opened. False means no sound card, or none available.</summary>
    public bool IsOpen => _device != IntPtr.Zero;

    /// <summary>Why it could not open, for the operator's log. Null when it did.</summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// Opens the default output device for one frame's worth of stereo at a time. Returns a
    /// closed instance rather than throwing when there is no sound device — a machine in a
    /// rack often has none, and that must not stop a recording.
    /// </summary>
    public static SpeakerOutput Open(int frameRate)
    {
        var output = new SpeakerOutput();
        output.Start(frameRate);
        return output;
    }

    private void Start(int frameRate)
    {
        int samplesPerFrame = SampleRate / Math.Max(1, frameRate);
        _bufferBytes = samplesPerFrame * Channels * BytesPerSample;

        var format = new WaveFormat
        {
            FormatTag = WaveFormatPcm,
            Channels = Channels,
            SamplesPerSec = SampleRate,
            AvgBytesPerSec = SampleRate * Channels * BytesPerSample,
            BlockAlign = Channels * BytesPerSample,
            BitsPerSample = BytesPerSample * 8,
            Size = 0,
        };

        IntPtr device = IntPtr.Zero;
        int rc;

        try
        {
            rc = waveOutOpen(ref device, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        }
        catch (DllNotFoundException)
        {
            Problem = "no Windows audio device interface";
            return;
        }

        if (rc != 0)
        {
            Problem = rc == 4 ? "no sound device is available" : $"the sound device could not be opened (error {rc})";
            return;
        }

        _device = device;
        _headers = new IntPtr[BufferCount];
        _buffers = new IntPtr[BufferCount];

        for (int i = 0; i < BufferCount; i++)
        {
            _buffers[i] = Marshal.AllocHGlobal(_bufferBytes);
            _headers[i] = Marshal.AllocHGlobal(HeaderBytes);

            for (int b = 0; b < HeaderBytes; b += 8) Marshal.WriteInt64(_headers[i], b, 0);

            Marshal.WriteIntPtr(_headers[i], HdrData, _buffers[i]);
            Marshal.WriteInt32(_headers[i], HdrLength, _bufferBytes);

            // Marked done up front so the first pass sees every buffer as free. waveOut sets
            // the same flag itself once it has finished playing one.
            Marshal.WriteInt32(_headers[i], HdrFlags, (int)WhdrDone);
        }
    }

    /// <summary>
    /// Queues one frame of a stereo pair. Never blocks and never throws: this is called from
    /// the receiver's slot loop, which is the clock for the whole recording.
    ///
    /// <paramref name="count"/> samples are taken from each of <paramref name="left"/> and
    /// <paramref name="right"/>. A pair that is not on the wire should simply not be pushed;
    /// the monitor then falls silent on its own.
    /// </summary>
    public void Push(short[] left, short[] right, int count, double gain = 1.0)
    {
        lock (_gate)
        {
            if (_device == IntPtr.Zero || _disposed || count <= 0) return;

            int queued = 0;
            for (int i = 0; i < BufferCount; i++)
                if ((Marshal.ReadInt32(_headers[i], HdrFlags) & WhdrDone) == 0) queued++;

            if (queued >= MaxQueued) { Dropped++; return; }

            // Round-robin from where the last one went, so buffers are reused in the order
            // the device finishes with them.
            int slot = -1;
            for (int i = 0; i < BufferCount; i++)
            {
                int candidate = (_next + i) % BufferCount;
                if ((Marshal.ReadInt32(_headers[candidate], HdrFlags) & WhdrDone) != 0) { slot = candidate; break; }
            }

            if (slot < 0) { Dropped++; return; }

            _next = (slot + 1) % BufferCount;

            int samples = Math.Min(count, _bufferBytes / (Channels * BytesPerSample));
            IntPtr buffer = _buffers[slot];

            for (int s = 0; s < samples; s++)
            {
                Marshal.WriteInt16(buffer, s * 4, Scale(s < left.Length ? left[s] : (short)0, gain));
                Marshal.WriteInt16(buffer, s * 4 + 2, Scale(s < right.Length ? right[s] : (short)0, gain));
            }

            int bytes = samples * Channels * BytesPerSample;
            Marshal.WriteInt32(_headers[slot], HdrLength, bytes);

            // Unprepare before re-preparing: the header is being reused, and waveOut refuses
            // to prepare one it already considers prepared.
            if ((Marshal.ReadInt32(_headers[slot], HdrFlags) & WhdrPrepared) != 0)
                waveOutUnprepareHeader(_device, _headers[slot], HeaderBytes);

            if (waveOutPrepareHeader(_device, _headers[slot], HeaderBytes) != 0) return;
            if (waveOutWrite(_device, _headers[slot], HeaderBytes) != 0) Dropped++;
        }
    }

    /// <summary>
    /// Monitor volume, 0 to 1, applied by the device rather than to the samples — so what is
    /// heard changes and what is recorded or transmitted does not.
    /// </summary>
    public void SetVolume(double volume)
    {
        lock (_gate)
        {
            if (_device == IntPtr.Zero || _disposed) return;

            // Left in the low word, right in the high word, both full scale at 0xFFFF.
            var scalar = (ushort)(Math.Clamp(volume, 0, 1) * ushort.MaxValue);

            waveOutSetVolume(_device, (uint)scalar | ((uint)scalar << 16));
        }
    }

    /// <summary>Drops everything queued, for when the source changes and the old sound is wrong.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_device != IntPtr.Zero && !_disposed) waveOutReset(_device);
        }
    }

    private static short Scale(short sample, double gain)
    {
        if (gain == 1.0) return sample;

        double scaled = sample * gain;
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            if (_device != IntPtr.Zero)
            {
                waveOutReset(_device);

                for (int i = 0; i < _headers.Length; i++)
                {
                    if (_headers[i] == IntPtr.Zero) continue;

                    if ((Marshal.ReadInt32(_headers[i], HdrFlags) & WhdrPrepared) != 0)
                        waveOutUnprepareHeader(_device, _headers[i], HeaderBytes);
                }

                waveOutClose(_device);
                _device = IntPtr.Zero;
            }

            foreach (IntPtr p in _headers) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            foreach (IntPtr p in _buffers) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);

            _headers = Array.Empty<IntPtr>();
            _buffers = Array.Empty<IntPtr>();
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(ref IntPtr device, int deviceId, ref WaveFormat format,
                                          IntPtr callback, IntPtr instance, int flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveOutSetVolume(IntPtr device, uint volume);
}
