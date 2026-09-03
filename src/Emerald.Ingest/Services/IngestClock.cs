using Emerald.Core;

namespace Emerald.Ingest;

/// <summary>
/// The clock an ingest is scheduled against.
///
/// Emerald already has exactly one realtime timecode source —
/// <see cref="TimecodeService"/>, disciplined against the station's timecode server — and
/// the ingest controller uses that one. This interface is not a second clock; it is the
/// seam that lets the scheduler be tested, and lets the module run on a bench where there
/// is no timecode server to reach.
/// </summary>
public interface IIngestClock
{
    /// <summary>The current timecode, or false when the source has not locked yet.</summary>
    bool TryGetCurrent(out Timecode now);

    /// <summary>Nominal integer rate; 0 until the source has been read once.</summary>
    int FrameRate { get; }

    /// <summary>True when the source reports itself locked, not merely reachable.</summary>
    bool IsLocked { get; }

    /// <summary>What the header reads under the clock: "LTC - LOCKED - 25 fps".</summary>
    string StatusText { get; }

    bool IsMock { get; }
}

/// <summary>Emerald's realtime timecode service, as the ingest controller sees it.</summary>
public sealed class TimecodeServiceClock : IIngestClock
{
    private readonly TimecodeService _service;

    public TimecodeServiceClock(TimecodeService service) => _service = service;

    public bool IsMock => false;

    public bool TryGetCurrent(out Timecode now) => _service.TryGetCurrent(out now);

    public int FrameRate => _service.FrameRate;

    public bool IsLocked =>
        _service.State == TimecodeLinkState.Online &&
        string.Equals(_service.LastResponse?.SourceStatus, "LOCKED", StringComparison.OrdinalIgnoreCase);

    public string StatusText => _service.State switch
    {
        TimecodeLinkState.Online =>
            $"{_service.LastResponse?.TimecodeType ?? "TC"} - {_service.LastResponse?.SourceStatus ?? "?"}" +
            $" - {_service.FrameRate} fps",
        TimecodeLinkState.Offline => "offline",
        _ => "connecting...",
    };
}

/// <summary>
/// A clock derived from the machine's own time, for bench work.
///
/// It is not disciplined against anything and makes no claim to be: <see cref="IsLocked"/>
/// is false and the status says FREE RUN, so nothing on screen can be mistaken for a locked
/// station clock. It exists so the queue and the scheduler can be exercised without a
/// timecode server, and for nothing else.
/// </summary>
public sealed class SystemIngestClock : IIngestClock
{
    public SystemIngestClock(int frameRate = 25) => FrameRate = frameRate <= 0 ? 25 : frameRate;

    public bool IsMock => true;
    public int FrameRate { get; }
    public bool IsLocked => false;
    public string StatusText => $"SYSTEM - FREE RUN - {FrameRate} fps";

    public bool TryGetCurrent(out Timecode now)
    {
        DateTime t = DateTime.Now;

        long frames = ((t.Hour * 60L + t.Minute) * 60L + t.Second) * FrameRate
                    + t.Millisecond * FrameRate / 1000;

        now = new Timecode(frames, FrameRate);
        return true;
    }
}
