using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Emerald.Video;

/// <summary>
/// One tick per frame without a card to set the pace: what the receiver's slots or the
/// transmitter's free slots are to everything else, for an IP stream coming in or a picture going
/// out with no SDI board.
///
/// Every tick is due at a moment counted from the start - start + n frames - not from the last
/// tick, so a late wake-up is made up on the next one and the clock never drifts. A wait that
/// comes back two frames late (a debugger, a long pause) starts the count again from there
/// rather than racing to catch up, and is counted in <see cref="Slipped"/>.
///
/// Windows sleeps in steps of about 15 ms unless asked otherwise, which is no use for 40 ms
/// frames. The wait sleeps on a high-resolution timer until about a millisecond before the tick
/// and spins the rest. Used from one thread.
/// </summary>
public sealed class FrameClock : IDisposable
{
    private readonly double _ticksPerFrame;
    private long _start;
    private long _n;
    private IntPtr _timer;

    /// <summary>How many times the clock fell behind by more than two frames and started again.</summary>
    public long Slipped { get; private set; }

    public int FrameRate { get; }

    public FrameClock(int frameRate)
    {
        FrameRate = Math.Max(1, frameRate);
        _ticksPerFrame = Stopwatch.Frequency / (double)FrameRate;

        try { _timer = CreateWaitableTimerExW(IntPtr.Zero, null, HighResolution, TimerAllAccess); }
        catch { _timer = IntPtr.Zero; }

        Restart();
    }

    /// <summary>Counts from now: the next tick is a frame away.</summary>
    public void Restart()
    {
        _start = Stopwatch.GetTimestamp();
        _n = 0;
    }

    /// <summary>Waits for the next frame's moment.</summary>
    public void Wait()
    {
        _n++;
        long due = _start + (long)(_n * _ticksPerFrame);
        long now = Stopwatch.GetTimestamp();

        if (now - due > 2 * _ticksPerFrame)
        {
            Slipped++;
            Restart();
            return;
        }

        while (true)
        {
            long left = due - Stopwatch.GetTimestamp();
            if (left <= 0) return;

            double ms = left * 1000.0 / Stopwatch.Frequency;

            if (ms > 2) Sleep(ms - 1);
            else Thread.Yield();
        }
    }

    private void Sleep(double ms)
    {
        if (_timer != IntPtr.Zero)
        {
            // Negative: relative, in 100 ns units.
            long due = -(long)(ms * 10_000);
            if (SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                WaitForSingleObject(_timer, 1000);
                return;
            }
        }

        Thread.Sleep(1);
    }

    public void Dispose()
    {
        if (_timer != IntPtr.Zero) CloseHandle(_timer);
        _timer = IntPtr.Zero;
    }

    private const uint HighResolution = 0x2;       // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Windows 10 1803 on
    private const uint TimerAllAccess = 0x1F0003;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion,
                                                IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
