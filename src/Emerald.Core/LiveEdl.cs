namespace Emerald.Core;

/// <summary>Why an EDL revision was sent.</summary>
public enum EdlSubmissionKind
{
    /// <summary>The timeline changed; playback is told ahead of any edit reaching the no edit zone.</summary>
    Update,

    /// <summary>
    /// An edit's first frame has reached the no edit zone, or an edit already there has changed:
    /// from here playback is committed to it, so this revision is the one that counts.
    /// </summary>
    Conform,

    /// <summary>The last revision was not acknowledged, and the latest state is being sent again.</summary>
    Retry,
}

/// <summary>
/// One complete version of the EDL, as handed to playback.
///
/// <b>Whole, not a delta.</b> Every revision carries every cue, so applying one is replacing the
/// list - there is no instruction that could be applied twice, or half applied, or applied on top
/// of the wrong predecessor. What makes an older revision unable to overwrite a newer one is
/// <see cref="Version"/>, which only ever grows.
/// </summary>
/// <param name="Version">Grows by at least one with every revision issued. Playback refuses anything not above what it has.</param>
/// <param name="TransactionId">Unique per send, so the same revision delivered twice is recognised rather than applied again.</param>
/// <param name="EditIds">The edits this revision conforms, for a <see cref="EdlSubmissionKind.Conform"/>; empty otherwise.</param>
public sealed record EdlRevision(
    long Version,
    string TransactionId,
    EdlSubmissionKind Kind,
    IReadOnlyList<LiveCue> Cues,
    IReadOnlyList<AudioEdit> AudioEdits,
    DateTime IssuedAt,
    IReadOnlyList<string> EditIds);

public enum EdlVerdict
{
    /// <summary>Taken. Playback will use it from its next frame; <see cref="IEdlPlayback.AppliedVersion"/> says when it has.</summary>
    Accepted,

    /// <summary>The same transaction again. Nothing is re-applied.</summary>
    Duplicate,

    /// <summary>Playback already has this version or a newer one. Nothing changes.</summary>
    Stale,

    /// <summary>It would change frames playback has already sent, or is sending. Nothing changes.</summary>
    TooLate,

    /// <summary>Nothing is on air to take it.</summary>
    NotLive,
}

/// <summary>What playback said to a revision.</summary>
public readonly record struct EdlReceipt(EdlVerdict Verdict, long Version, string TransactionId, string? Reason)
{
    public bool Taken => Verdict is EdlVerdict.Accepted or EdlVerdict.Duplicate;
}

/// <summary>
/// The playback side of the EDL handshake. <see cref="LiveAir"/> in the application; anything in
/// a test.
///
/// <b>Accepted is not applied.</b> <see cref="Submit"/> answers whether a revision was taken;
/// <see cref="AppliedVersion"/> moves only once the transmitter has actually read it on its frame
/// loop. A sender must not treat the first as the second.
/// </summary>
public interface IEdlPlayback
{
    /// <summary>The newest version taken. Zero before any.</summary>
    long AcceptedVersion { get; }

    /// <summary>The newest version the transmitter has picked up and is playing to. Zero before any.</summary>
    long AppliedVersion { get; }

    EdlReceipt Submit(EdlRevision revision);
}

/// <summary>Where two versions of the EDL first differ, which is what decides whether a change is still in time.</summary>
public static class EdlDiff
{
    /// <summary>
    /// The first frame of the recording whose output differs between <paramref name="before"/>
    /// and <paramref name="after"/>, or null when they play the same.
    ///
    /// A blank that kept its start and only moved its end first differs at the nearer of its two
    /// ends: the frames before that are black either way. That is what lets an edit whose start
    /// has already been conformed still be lengthened or shortened.
    /// </summary>
    public static long? EarliestChange(IReadOnlyList<LiveCue> before, IReadOnlyList<LiveCue> after)
    {
        long? earliest = null;

        void Consider(long frame) => earliest = earliest is { } e ? Math.Min(e, frame) : frame;

        foreach (LiveCue cue in before)
        {
            LiveCue? now = after.FirstOrDefault(c => c.Id == cue.Id);

            if (now is null) Consider(cue.RecordingFrame);
            else if (EarliestChange(cue, now) is { } at) Consider(at);
        }

        foreach (LiveCue cue in after)
            if (!before.Any(c => c.Id == cue.Id))
                Consider(cue.RecordingFrame);

        return earliest;
    }

    /// <summary>Where one cue's two versions first differ, or null when they are the same.</summary>
    public static long? EarliestChange(LiveCue before, LiveCue after)
    {
        if (before == after) return null;

        if (before.Mode == IngestMode.Blank && after.Mode == IngestMode.Blank &&
            before.RecordingFrame == after.RecordingFrame)
            return Math.Min(before.EndFrame, after.EndFrame);

        return Math.Min(before.RecordingFrame, after.RecordingFrame);
    }

    /// <summary>
    /// The same question for two versions of one placement: the first frame, in the placements'
    /// own planned frames, at which they put different things on air. Null when the same.
    /// </summary>
    public static long? EarliestChange(IngestPlan? before, IngestPlan? after)
    {
        if (Equals(before, after)) return null;

        if (before is { Mode: IngestMode.Blank } a && after is { Mode: IngestMode.Blank } b &&
            a.StartFrame == b.StartFrame)
            return a.StartFrame + Math.Min(a.Frames, b.Frames);

        return (before, after) switch
        {
            ({ } x, { } y) => Math.Min(x.StartFrame, y.StartFrame),
            ({ } x, null) => x.StartFrame,
            (null, { } y) => y.StartFrame,
            _ => null,
        };
    }
}
