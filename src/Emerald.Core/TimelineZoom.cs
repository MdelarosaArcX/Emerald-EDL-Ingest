namespace Emerald.Core;

/// <summary>
/// One rung of the timeline's zoom ladder: how much time is across the window, how far an
/// arrow key moves, and how often the ruler is marked.
///
/// Everything is in frames rather than seconds. A timeline that deals in seconds cannot
/// address a single frame without rounding, and the whole point of the deepest rung is that
/// one press of an arrow key moves exactly one frame.
/// </summary>
public readonly record struct ZoomLevel(string Name, long SpanFrames, long StepFrames, long TickFrames)
{
    /// <summary>What an arrow key does here, for the operator to read off the bar.</summary>
    public string StepName { get; init; } = "";

    public override string ToString() => Name;
}

/// <summary>
/// The zoom ladder for the Live Edit timeline.
///
/// A rung is a <b>span</b> — how much of the recording is across the window — rather than a
/// magnification, because that is what an operator is actually choosing: "show me an hour",
/// "show me a second". The ladder runs from a whole day down to a handful of frames, and the
/// arrow-key step is a property of the rung rather than a fixed amount: at the hour view a key
/// press moves a minute, and at the bottom it moves a single frame. Stepping by a frame across
/// an hour-wide window would not move the playhead by a whole pixel.
///
/// Built for a frame rate rather than fixed, so the same ladder is right at 25 and at 50. It
/// is pure and lives here rather than in the window because the arithmetic — which rung is the
/// default, what a key press does, where a frame sits in pixels — is the part worth testing,
/// and none of it needs a window to be open.
/// </summary>
public static class TimelineZoom
{
    /// <summary>The longest span offered. A day of recording is the most anything here holds.</summary>
    public const int MaxSpanSeconds = 24 * 60 * 60;

    /// <summary>What the timeline opens at.</summary>
    public const int DefaultSpanSeconds = 60 * 60;

    /// <summary>
    /// The ladder, widest first, for a given frame rate.
    ///
    /// The last rung is the one the operator asked for by name: at full zoom the timeline is
    /// measuring single frames, the ruler is marked every frame, and an arrow key moves one.
    /// </summary>
    public static IReadOnlyList<ZoomLevel> Ladder(int frameRate)
    {
        long f = Math.Max(1, frameRate);

        long Minutes(long n) => n * 60 * f;
        long Seconds(long n) => n * f;

        return new[]
        {
            new ZoomLevel("24 hours", Minutes(24 * 60), Minutes(60), Minutes(60)) { StepName = "1 hour" },
            new ZoomLevel("12 hours", Minutes(12 * 60), Minutes(30), Minutes(60)) { StepName = "30 min" },
            new ZoomLevel("6 hours", Minutes(6 * 60), Minutes(15), Minutes(30)) { StepName = "15 min" },
            new ZoomLevel("3 hours", Minutes(3 * 60), Minutes(10), Minutes(15)) { StepName = "10 min" },
            new ZoomLevel("1 hour", Minutes(60), Minutes(1), Minutes(5)) { StepName = "1 min" },
            new ZoomLevel("30 min", Minutes(30), Seconds(30), Minutes(5)) { StepName = "30 s" },
            new ZoomLevel("10 min", Minutes(10), Seconds(10), Minutes(1)) { StepName = "10 s" },
            new ZoomLevel("5 min", Minutes(5), Seconds(5), Seconds(30)) { StepName = "5 s" },
            new ZoomLevel("1 min", Minutes(1), Seconds(1), Seconds(10)) { StepName = "1 s" },
            new ZoomLevel("30 s", Seconds(30), Seconds(1), Seconds(5)) { StepName = "1 s" },
            new ZoomLevel("10 s", Seconds(10), f / 2, Seconds(1)) { StepName = "½ s" },
            new ZoomLevel("2 s", Seconds(2), 5, Seconds(1)) { StepName = "5 frames" },
            new ZoomLevel("1 s", Seconds(1), 1, f / 5) { StepName = "1 frame" },
            new ZoomLevel("frames", 20, 1, 1) { StepName = "1 frame" },
        };
    }

    /// <summary>Which rung the timeline opens at: one hour across the window.</summary>
    public static int DefaultIndex(int frameRate)
    {
        IReadOnlyList<ZoomLevel> ladder = Ladder(frameRate);

        for (int i = 0; i < ladder.Count; i++)
            if (ladder[i].SpanFrames == (long)DefaultSpanSeconds * Math.Max(1, frameRate))
                return i;

        return ladder.Count / 2;
    }

    /// <summary>
    /// Moves up or down the ladder and stays on it. Positive zooms in — less time across the
    /// window, more detail — which is the direction the scroll wheel and the + key mean.
    /// </summary>
    public static int Step(int index, int by, int frameRate) =>
        Math.Clamp(index + by, 0, Ladder(frameRate).Count - 1);

    /// <summary>
    /// Where the window starts, so <paramref name="playhead"/> sits in it.
    ///
    /// The playhead is kept centred while there is recording on both sides of it, and the
    /// window is pinned to the ends otherwise — scrolling past the start into empty space is
    /// disorienting, and a playhead that stops moving because the view ran out is worse.
    /// A recording shorter than the span starts at zero: there is nothing to scroll.
    /// </summary>
    public static long WindowStart(long playhead, long spanFrames, long totalFrames)
    {
        if (spanFrames <= 0) return 0;
        if (totalFrames <= spanFrames) return 0;

        return Math.Clamp(playhead - spanFrames / 2, 0, totalFrames - spanFrames);
    }

    /// <summary>
    /// Where a frame lands across a window of <paramref name="width"/> pixels, or null when it
    /// is outside the window and should not be drawn.
    /// </summary>
    public static double? XOf(long frame, long windowStart, long spanFrames, double width)
    {
        if (spanFrames <= 0 || width <= 0) return null;

        double at = (frame - windowStart) / (double)spanFrames * width;

        return at < 0 || at > width ? null : at;
    }

    /// <summary>The frame under a pixel, for a click on the timeline. Clamped to the recording.</summary>
    public static long FrameAt(double x, long windowStart, long spanFrames, double width, long totalFrames)
    {
        if (spanFrames <= 0 || width <= 0) return windowStart;

        long frame = windowStart + (long)Math.Round(x / width * spanFrames);

        return Math.Clamp(frame, 0, Math.Max(0, totalFrames));
    }

    // ------------------------------------------------------------------ the station day

    /// <summary>A whole day, in frames. The length of a timeline laid out on the station clock.</summary>
    public static long DayFrames(int frameRate) => (long)MaxSpanSeconds * Math.Max(1, frameRate);

    /// <summary>
    /// Where a moment in the day sits, in frames from midnight.
    ///
    /// A timeline of captured media is a timeline of the day: a segment that began at 11:34:52
    /// belongs at 11:34:52 and nowhere else, so that what is on air can be pointed at on the
    /// same ruler. Frames from midnight is the one coordinate both a file's name and the station
    /// clock can be turned into.
    /// </summary>
    public static long FrameOfDay(TimeSpan timeOfDay, int frameRate)
    {
        long rate = Math.Max(1, frameRate);
        long day = DayFrames(frameRate);

        long frame = (long)Math.Round(timeOfDay.TotalSeconds * rate);

        // Wrapped rather than clamped, so a delay that reaches back over midnight lands where it
        // belongs instead of piling up at either end of the day.
        frame %= day;
        return frame < 0 ? frame + day : frame;
    }

    public static long FrameOfDay(DateTime at, int frameRate) =>
        FrameOfDay(at.TimeOfDay, frameRate);

    /// <summary>The moment a frame of the day is, for a ruler label.</summary>
    public static TimeSpan TimeOfFrame(long frame, int frameRate) =>
        TimeSpan.FromSeconds(frame / (double)Math.Max(1, frameRate));

    /// <summary>
    /// Where the feed on air sits on the day, given the delay it is running behind.
    ///
    /// The whole point of the marker: the transmitter is carrying what the receiver produced a
    /// delay ago, so on air is the clock minus the delay, and that is a place on this timeline
    /// rather than a number in a corner.
    /// </summary>
    public static long OnAirFrame(TimeSpan stationTime, TimeSpan behind, int frameRate) =>
        FrameOfDay(stationTime - behind, frameRate);

}
