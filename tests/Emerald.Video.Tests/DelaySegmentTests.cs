using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// What tidal lock says as each segment's worth of the recording reaches the transmitter.
///
/// The operator asked for it in these terms: the first picture goes to air, then every two
/// minutes the next file's worth does. That is a function of how far into the recording the
/// frame just sent is, and nothing else.
/// </summary>
public class DelaySegmentTests
{
    private const long Segment = 120 * 25;   // two minutes at 25

    [Fact]
    public void The_first_segment_is_not_announced_here_because_the_on_air_line_is_it()
    {
        long last = 0;

        Assert.Null(DelayTransmitter.SegmentReached(0, Segment, ref last));
        Assert.Null(DelayTransmitter.SegmentReached(1, Segment, ref last));
        Assert.Null(DelayTransmitter.SegmentReached(Segment - 1, Segment, ref last));
    }

    [Fact]
    public void The_second_segment_is_announced_on_its_first_frame_and_once_only()
    {
        long last = 0;
        DelayTransmitter.SegmentReached(0, Segment, ref last);

        Assert.Equal(2, DelayTransmitter.SegmentReached(Segment, Segment, ref last));
        Assert.Null(DelayTransmitter.SegmentReached(Segment + 1, Segment, ref last));
        Assert.Null(DelayTransmitter.SegmentReached(2 * Segment - 1, Segment, ref last));
        Assert.Equal(3, DelayTransmitter.SegmentReached(2 * Segment, Segment, ref last));
    }

    /// <summary>A resync jumps the read position forward. That is one cut, and one line, not one per segment skipped.</summary>
    [Fact]
    public void Jumping_forward_announces_the_segment_landed_in_not_every_one_passed()
    {
        long last = 0;
        DelayTransmitter.SegmentReached(Segment, Segment, ref last);   // now in 2

        Assert.Equal(6, DelayTransmitter.SegmentReached(5 * Segment + 40, Segment, ref last));
        Assert.Null(DelayTransmitter.SegmentReached(5 * Segment + 41, Segment, ref last));
    }

    [Fact]
    public void With_no_segment_length_known_nothing_is_said()
    {
        long last = 0;

        Assert.Null(DelayTransmitter.SegmentReached(Segment * 3, 0, ref last));
        Assert.Equal(0, last);
    }
}
