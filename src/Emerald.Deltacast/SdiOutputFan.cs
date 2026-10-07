namespace Emerald.Deltacast;

/// <summary>
/// What everything that transmits needs of an output, so one picture can go to one port or to
/// several without the thing producing it knowing which.
///
/// Deliberately four members. <see cref="SdiOutput"/> has rather more — slot counters, the board
/// and channel it is on — and none of it means anything for a group of outputs, so none of it is
/// here.
/// </summary>
public interface ISdiOutput : IDisposable
{
    VideoFormat Format { get; }

    /// <summary>
    /// Sends one frame, blocking until the card is ready for it. False means the card stopped
    /// accepting frames and whatever is driving this should stop.
    /// </summary>
    bool PushFrame(byte[] frame, IReadOnlyList<short[]>? audioChannels = null);

    /// <summary>Legal black in this format, for holding the line.</summary>
    byte[] BlackFrame();
}

/// <summary>
/// One picture to several TX ports at once.
///
/// <b>Why this paces correctly.</b> Every port is on the same board and therefore the same
/// genlock, so they free their slots in step. Pushing to the first blocks for a frame; by the
/// time it returns the rest already have a slot free and take theirs immediately. So the loop is
/// still paced by one frame per frame, exactly as a single output paces it, and the frame that
/// goes out of port two is the same array that went out of port one.
///
/// <b>Why it is one board.</b> Ports on different boards are genlocked separately, so pushing to
/// both from one loop would be paced by whichever is slower and the other would drift — a drift
/// nothing here can correct, because there is one read of the picture and two clocks asking for
/// it. The deck therefore offers the ports of the selected board and no others.
///
/// If any port stops taking frames the whole group reports it, because a group that quietly
/// carried on with three of four outputs would be an output off air that nothing mentioned.
/// </summary>
public sealed class SdiOutputFan : ISdiOutput
{
    private readonly List<SdiOutput> _outputs = new();

    public VideoFormat Format { get; }

    /// <summary>The ports actually open, in the order they were asked for.</summary>
    public IReadOnlyList<int> Channels => _outputs.Select(o => o.TxChannel).ToList();

    /// <summary>The port that stopped taking frames, once one has. -1 until then.</summary>
    public int FailedChannel { get; private set; } = -1;

    private SdiOutputFan(VideoFormat format) => Format = format;

    /// <summary>
    /// Opens every port given, on one board.
    ///
    /// All or nothing: if the third of four cannot be opened, the two already open are closed
    /// again and the exception is left to the caller. A transmitter half on air is worse than one
    /// that refused to start, because only one of those gets noticed.
    /// </summary>
    public static SdiOutputFan Open(uint boardIndex, IReadOnlyList<int> txChannels, VideoFormat format)
    {
        if (txChannels.Count == 0)
            throw new SdiOutputException("No TX port was selected.");

        var fan = new SdiOutputFan(format);

        try
        {
            foreach (int channel in txChannels.Distinct())
                fan._outputs.Add(SdiOutput.Open(boardIndex, channel, format));
        }
        catch
        {
            fan.Dispose();
            throw;
        }

        return fan;
    }

    public bool PushFrame(byte[] frame, IReadOnlyList<short[]>? audioChannels = null)
    {
        bool all = true;

        foreach (SdiOutput output in _outputs)
        {
            if (output.PushFrame(frame, audioChannels)) continue;

            // Named, because "the card stopped accepting frames" is not actionable when there
            // are four of them and only one has.
            if (FailedChannel < 0) FailedChannel = output.TxChannel;
            all = false;
        }

        return all;
    }

    /// <summary>Black for the format, which is the same for every port in the group.</summary>
    public byte[] BlackFrame() =>
        _outputs.Count > 0
            ? _outputs[0].BlackFrame()
            : throw new SdiOutputException("The output group has no ports open.");

    public void Dispose()
    {
        foreach (SdiOutput output in _outputs) output.Dispose();

        _outputs.Clear();
    }
}
