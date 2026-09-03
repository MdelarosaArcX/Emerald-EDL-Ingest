namespace Emerald.Core;

/// <summary>
/// The one place the SOM / EOM / Duration relationship is decided.
///
/// Every module that schedules against the clock — the Ingest Controller most of all —
/// asks these questions, and a broadcast operator has to be able to point at a single
/// answer for each. Scattering the arithmetic through ViewModels is how two screens end up
/// disagreeing about when a recording stops.
///
/// The convention, stated once:
///
///   <b>Start timecode</b> is when the recorder rolls, on the station clock. Exactly then —
///   there is no preroll and nothing is subtracted from it.
///   <b>Duration</b> is how long it records for.
///   <b>EOM</b> is therefore start + duration, and is when it stops.
///   <b>SOM</b> is not a time at all: it is the timecode written into the head of the
///   recorded file, so the clip can carry whatever numbering the station edits against.
///
/// So:
///
///   start 20:57:26:00, duration 00:15:00:00, SOM 01:00:00:00
///     -> rolls at       20:57:26:00
///     -> stops at       21:12:26:00   (EOM)
///     -> the file reads 01:00:00:00 at its first frame
///
/// Everything is frame arithmetic on <see cref="Timecode"/>, never DateTime, and every
/// result wraps at midnight the way a timecode clock does.
/// </summary>
public interface ITimecodeCalculationService
{
    /// <summary>EOM from a duration measured off the start timecode.</summary>
    Timecode CalculateEomFromDuration(Timecode reference, Timecode duration);

    /// <summary>The inverse: how long the recording runs to reach a given EOM.</summary>
    Timecode CalculateDurationFromEom(Timecode reference, Timecode eom);

    /// <summary>
    /// Frames from <paramref name="from"/> forward to <paramref name="to"/>, going the long
    /// way round midnight rather than backwards. 0 when they are the same instant.
    /// </summary>
    long FramesUntil(Timecode from, Timecode to);
}

/// <inheritdoc cref="ITimecodeCalculationService"/>
public sealed class TimecodeCalculationService : ITimecodeCalculationService
{
    /// <summary>A durations-and-offsets calculator is stateless; one instance serves everything.</summary>
    public static ITimecodeCalculationService Instance { get; } = new TimecodeCalculationService();

    public Timecode CalculateEomFromDuration(Timecode reference, Timecode duration) =>
        reference.AddWrapping(duration.TotalFrames);

    public Timecode CalculateDurationFromEom(Timecode reference, Timecode eom)
    {
        // A duration is a length, not a position, so it is counted forward from the
        // reference. An EOM earlier in the day than the reference is tomorrow's, which is
        // exactly how an overnight record behaves.
        long frames = FramesUntil(reference, eom);
        return new Timecode(frames, reference.Rate);
    }

    public long FramesUntil(Timecode from, Timecode to)
    {
        if (from.Rate <= 0) return 0;

        long perDay = 24L * 3600L * from.Rate;
        long delta = (to.Rebase(from.Rate).TotalFrames - from.TotalFrames) % perDay;
        return delta < 0 ? delta + perDay : delta;
    }
}
