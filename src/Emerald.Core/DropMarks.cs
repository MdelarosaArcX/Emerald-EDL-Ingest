namespace Emerald.Core;

/// <summary>Where a dropped frame came from, because the two mean different things.</summary>
public enum DropKind
{
    /// <summary>
    /// The encoder could not keep up and frames were never written. There is a hole in the
    /// recording at this moment — the material is gone.
    /// </summary>
    Capture,

    /// <summary>
    /// The transmitter repeated or skipped a frame to hold the delay. The recording is intact;
    /// what went to air at this moment was not quite what was recorded.
    /// </summary>
    OnAir,
}

/// <summary>One moment at which frames were lost.</summary>
/// <param name="At">
/// Wall-clock time, not a frame number. The producers do not share a frame of reference — the
/// recorder counts from the start of its recording and the transmitter from the start of the
/// delay — but they do share a clock, and the timeline that draws these knows how to put a time
/// on the day.
/// </param>
/// <param name="Frames">How many frames went at this moment.</param>
public readonly record struct DropMark(DateTime At, int Frames, DropKind Kind)
{
    public string Label => Kind == DropKind.Capture
        ? $"{Frames} frame(s) not recorded"
        : $"{Frames} frame(s) repeated or skipped on air";
}

/// <summary>
/// Every moment frames were lost, for marking on a timeline.
///
/// <b>Why this exists separately from the log.</b> Drops are already written to the record, with
/// their counts and their times, and that is the right place to answer "what happened last
/// Tuesday". It is the wrong place to answer "where on this timeline" — that needs the moments
/// as data rather than as prose, available now, cheap to ask for on every redraw.
///
/// Capped, and the oldest go first. A plant losing frames steadily for a day would otherwise
/// accumulate them without limit for a marker that is three pixels wide.
/// </summary>
public sealed class DropMarks
{
    /// <summary>The one every producer writes to and every timeline reads.</summary>
    public static readonly DropMarks Shared = new();

    /// <summary>
    /// How many moments are kept. Thousands, because each is a moment rather than a frame — a
    /// stall of four hundred frames is one of these.
    /// </summary>
    public const int Limit = 4000;

    private readonly object _gate = new();
    private readonly Queue<DropMark> _marks = new();

    /// <summary>Raised when a drop is recorded, so a timeline can redraw.</summary>
    public event Action? Changed;

    public int Count
    {
        get { lock (_gate) return _marks.Count; }
    }

    /// <summary>
    /// Records a moment at which frames were lost.
    ///
    /// Called from the capture thread and the transmitter thread, both real-time loops, so it
    /// takes a short lock and does nothing else. Zero or negative counts are ignored rather than
    /// marked: a marker claiming no frames were lost would be worse than none.
    /// </summary>
    public void Add(DateTime at, int frames, DropKind kind)
    {
        if (frames <= 0) return;

        lock (_gate)
        {
            _marks.Enqueue(new DropMark(at, frames, kind));

            while (_marks.Count > Limit) _marks.Dequeue();
        }

        Changed?.Invoke();
    }

    /// <summary>Everything recorded on one day, oldest first.</summary>
    public IReadOnlyList<DropMark> On(DateTime day)
    {
        lock (_gate) return _marks.Where(m => m.At.Date == day.Date).ToList();
    }

    /// <summary>Everything recorded, oldest first.</summary>
    public IReadOnlyList<DropMark> All()
    {
        lock (_gate) return _marks.ToList();
    }

    /// <summary>Starts again — a new recording is not judged on the last one's losses.</summary>
    public void Clear()
    {
        bool any;

        lock (_gate)
        {
            any = _marks.Count > 0;
            _marks.Clear();
        }

        if (any) Changed?.Invoke();
    }
}
