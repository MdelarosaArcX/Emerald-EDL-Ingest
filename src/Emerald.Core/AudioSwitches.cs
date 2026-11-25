namespace Emerald.Core;

/// <summary>
/// From a frame on, which of the recording's sound tracks plays: 0 is the first pair the receiver
/// carried (CH 1-2), 1 the second, and so on. It holds until the next switch.
///
/// In frames, like <see cref="AudioEdit"/>: Live Edit keeps these in captured frames of its day,
/// and the transmitter is handed them in frames of the recording.
/// </summary>
public sealed record AudioSwitch(long StartFrame, int Track);

/// <summary>
/// The rules for choosing the sound that plays. Pure, so they can be tested without a card: Live
/// Edit uses them to lay switches down, the transmitter to find the track of the frame it is about
/// to send.
/// </summary>
public static class AudioSwitches
{
    /// <summary>What plays where nothing has been chosen: the first track.</summary>
    public const int Default = 0;

    /// <summary>The track that plays at a frame.</summary>
    public static int TrackAt(long frame, IReadOnlyList<AudioSwitch> switches)
    {
        int track = Default;

        foreach (AudioSwitch at in switches)
        {
            if (at.StartFrame > frame) break;
            track = at.Track;
        }

        return track;
    }

    /// <summary>
    /// The switches with <paramref name="track"/> playing from <paramref name="from"/> until the
    /// next switch already placed after it. A switch already at that very frame is replaced, and
    /// one that would change nothing is not kept.
    /// </summary>
    public static IReadOnlyList<AudioSwitch> Set(IReadOnlyList<AudioSwitch> switches, long from, int track) =>
        Tidy(switches.Where(s => s.StartFrame != from).Append(new AudioSwitch(from, Math.Max(0, track))));

    /// <summary>The switches without the one at <paramref name="frame"/>: the track before it plays on.</summary>
    public static IReadOnlyList<AudioSwitch> Remove(IReadOnlyList<AudioSwitch> switches, long frame) =>
        Tidy(switches.Where(s => s.StartFrame != frame));

    /// <summary>
    /// What to hand a transmitter that already holds <paramref name="air"/>: what it holds up to
    /// <paramref name="from"/>, which has gone out or is about to and cannot be changed, and
    /// <paramref name="wanted"/> from there on.
    ///
    /// A change the transmitter was never given - the recording went to air before it, or another
    /// window made it - is then simply in force from here on, rather than making every later change
    /// look like an attempt to rewrite what has aired.
    /// </summary>
    public static IReadOnlyList<AudioSwitch> From(long from, IReadOnlyList<AudioSwitch> air, IReadOnlyList<AudioSwitch> wanted) =>
        Tidy(air.Where(s => s.StartFrame < from)
                .Append(new AudioSwitch(from, TrackAt(from, wanted)))
                .Concat(wanted.Where(s => s.StartFrame > from)));

    /// <summary>
    /// The first frame at which two sets of switches play different tracks, or null when they play
    /// the same throughout. What an edit changes, so it can be refused where air has gone by.
    /// </summary>
    public static long? FirstDifference(IReadOnlyList<AudioSwitch> a, IReadOnlyList<AudioSwitch> b)
    {
        // The track is constant between switches, so checking every switch of both is enough.
        foreach (long frame in a.Concat(b).Select(s => s.StartFrame).Distinct().OrderBy(f => f))
            if (TrackAt(frame, a) != TrackAt(frame, b))
                return frame;

        return null;
    }

    /// <summary>Sorted, without the ones that switch to the track already playing.</summary>
    private static IReadOnlyList<AudioSwitch> Tidy(IEnumerable<AudioSwitch> switches)
    {
        var kept = new List<AudioSwitch>();

        foreach (AudioSwitch at in switches.OrderBy(s => s.StartFrame))
            if (at.Track != (kept.Count > 0 ? kept[^1].Track : Default))
                kept.Add(at);

        return kept;
    }
}
