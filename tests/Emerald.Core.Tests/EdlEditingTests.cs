using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// Live Edit's edits as EDL modifications: a deleted scene goes to air as black and silence for
/// its whole length without moving anything, every revision of the EDL is versioned so an older
/// one cannot overwrite a newer, an edit is conformed when its first frame reaches the no edit
/// zone and can still be lengthened or shortened after that, and every step is logged.
///
/// The rate is passed in everywhere rather than assumed, and several tests run at more than one,
/// because nothing here is allowed to know it is 25.
/// </summary>
public sealed class EdlEditingTests
{
    // ------------------------------------------------------------------ helpers

    private static IngestItem Blank(long from, long to)
    {
        Assert.True(IngestPlacement.TryPlanBlank(from, to, out IngestPlan plan, out string? problem), problem);
        return new IngestItem("", plan);
    }

    private static LiveCue BlankCue(string id, long from, long to) =>
        new(id, IngestMode.Blank, null, from, to - from, to - from);

    private static EdlSelection Selection(long markIn, long markOut, int rate = 25, params EdlSegment[] segments) =>
        new(markIn, markOut, rate,
            EdlSources.Resolve(markIn, markOut - 1,
                               segments.Length > 0 ? segments : new[] { new EdlSegment("seg_1.mp4", 0, 1_000_000) }),
            "1 recording track(s)");

    private static EdlEditInfo BlankEdit(string id, long markIn, long markOut, int rate = 25) =>
        new(id, IngestMode.Blank, Selection(markIn, markOut, rate), "", $"blank {markIn}-{markOut}");

    /// <summary>A playback that takes everything newer and acknowledges only when told to.</summary>
    private sealed class FakePlayback : IEdlPlayback
    {
        public long AcceptedVersion { get; private set; }
        public long AppliedVersion { get; private set; }
        public List<EdlRevision> Received { get; } = new();
        public EdlVerdict? Refuse { get; set; }

        public EdlReceipt Submit(EdlRevision revision)
        {
            Received.Add(revision);

            if (Refuse is { } verdict) return new EdlReceipt(verdict, revision.Version, revision.TransactionId, "refused in test");
            if (revision.Version <= AcceptedVersion) return new EdlReceipt(EdlVerdict.Stale, revision.Version, revision.TransactionId, "stale");

            AcceptedVersion = revision.Version;
            return new EdlReceipt(EdlVerdict.Accepted, revision.Version, revision.TransactionId, null);
        }

        public void PickUp() => AppliedVersion = AcceptedVersion;
    }

    private sealed class Line : IDelayLine
    {
        public int Width => 1920;
        public int Height => 1080;
        public int FrameRate => 25;
        public long FramesWritten => 0;
        public long TargetFrames => 0;
        public bool IsSealed => false;
        public long SealedAt => 0;
        public string Path => "test";
    }

    private static LiveAir OnAir()
    {
        var air = new LiveAir();
        air.Arm(TimeSpan.FromSeconds(30), TimeSpan.Zero, 0, new[] { 1 });
        air.RecordingRolled(new Line(), DateTime.Today);
        air.WentToAir();
        return air;
    }

    private static EdlRevision Revision(long version, params LiveCue[] cues) =>
        new(version, Guid.NewGuid().ToString("N"), EdlSubmissionKind.Update, cues, Array.Empty<AudioEdit>(),
            DateTime.UtcNow, Array.Empty<string>());

    private static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------ 1, 4: marks and frame-accurate lengths

    [Theory]
    [InlineData(25)]
    [InlineData(30)]
    [InlineData(50)]
    public void A_marked_range_is_in_to_out_with_the_out_frame_not_selected(int rate)
    {
        long markIn = 10L * rate;          // 00:00:10:00
        long markOut = 50L * rate;         // 00:00:50:00

        EdlSelection s = Selection(markIn, markOut, rate);

        Assert.Equal(40L * rate, s.DurationFrames);
        Assert.Equal(40.0, s.DurationSeconds, 9);
        Assert.Equal(markIn, s.FirstFrame);
        Assert.Equal(markOut - 1, s.LastFrame);
        Assert.Equal("00:00:10:00", s.MarkInTimecode);
        Assert.Equal("00:00:50:00", s.MarkOutTimecode);
    }

    [Fact]
    public void A_one_frame_range_is_one_frame_long()
    {
        EdlSelection s = Selection(1000, 1001);

        Assert.Equal(1, s.DurationFrames);
        Assert.Equal(1000, s.LastFrame);
        Assert.Equal(1, Assert.Single(s.Sources).Frames);
    }

    [Fact]
    public void Odd_lengths_are_counted_to_the_frame_not_rounded_to_the_second()
    {
        EdlSelection s = Selection(1000, 1000 + 1013, 30);

        Assert.Equal(1013, s.DurationFrames);
        Assert.Equal(1013 / 30.0, s.DurationSeconds, 9);
    }

    // ------------------------------------------------------------------ 5: across capture files

    [Fact]
    public void A_range_across_segments_names_every_file_and_exactly_which_of_its_frames()
    {
        EdlSegment[] segments =
        {
            new("a.mp4", 0, 250),
            new("b.mp4", 250, 250),
            new("c.mp4", 500, 250),
        };

        IReadOnlyList<EdlSourceRange> sources = EdlSources.Resolve(200, 549, segments);

        Assert.Collection(sources,
            s => { Assert.Equal("a.mp4", s.File); Assert.Equal(200, s.FirstFileFrame); Assert.Equal(249, s.LastFileFrame); Assert.Equal(50, s.Frames); },
            s => { Assert.Equal("b.mp4", s.File); Assert.Equal(0, s.FirstFileFrame); Assert.Equal(249, s.LastFileFrame); Assert.Equal(250, s.Frames); },
            s => { Assert.Equal("c.mp4", s.File); Assert.Equal(0, s.FirstFileFrame); Assert.Equal(49, s.LastFileFrame); Assert.Equal(50, s.Frames); });

        Assert.Equal(350, sources.Sum(s => s.Frames));
    }

    [Fact]
    public void A_stretch_nothing_recorded_is_listed_so_the_sources_still_add_up_to_the_range()
    {
        EdlSegment[] segments = { new("a.mp4", 0, 100), new("b.mp4", 150, 100) };

        IReadOnlyList<EdlSourceRange> sources = EdlSources.Resolve(50, 199, segments);

        Assert.Equal(new string?[] { "a.mp4", null, "b.mp4" }, sources.Select(s => s.File));
        Assert.Equal(150, sources.Sum(s => s.Frames));
        Assert.Equal(new[] { 50L, 100L, 150L }, sources.Select(s => s.FirstFrame));
    }

    [Fact]
    public void A_selection_across_files_reports_both_files_and_that_it_spans_them()
    {
        EdlSelection s = Selection(240, 260, 25, new EdlSegment("a.mp4", 0, 250), new EdlSegment("b.mp4", 250, 250));

        Assert.True(s.SpansFiles);
        Assert.Equal(new[] { "a.mp4", "b.mp4" }, s.SourceFiles);
    }

    // ------------------------------------------------------------------ 2, 3: delete is black and silence

    [Fact]
    public void Deleting_plans_black_and_silence_over_the_range_with_no_media_and_no_shift()
    {
        Assert.True(IngestPlacement.TryPlanBlank(1000, 2000, out IngestPlan plan, out _));

        Assert.Equal(IngestMode.Blank, plan.Mode);
        Assert.Equal(1000, plan.StartFrame);
        Assert.Equal(1000, plan.Frames);
        Assert.Equal(1000, plan.Covers);
        Assert.Equal(0, plan.ShiftsAfterBy);
        Assert.False(plan.HasVideo);
        Assert.False(plan.HasAudio);
        Assert.True(plan.HasNoMedia);
    }

    [Fact]
    public void Deleting_needs_a_marked_range()
    {
        Assert.False(IngestPlacement.TryPlanBlank(null, 2000, out _, out string? problem));
        Assert.NotNull(problem);
        Assert.False(IngestPlacement.TryPlanBlank(2000, 2000, out _, out _));
        Assert.False(IngestPlacement.TryPlanBlank(2000, 1000, out _, out _));
    }

    [Fact]
    public void Every_frame_of_a_deleted_scene_goes_out_black_and_none_either_side()
    {
        LiveCue[] cues = { BlankCue("d", 1000, 2000) };

        Assert.Null(LiveCues.BlankAt(999, cues));
        Assert.Same(cues[0], LiveCues.BlankAt(1000, cues));
        Assert.Same(cues[0], LiveCues.BlankAt(1999, cues));
        Assert.Null(LiveCues.BlankAt(2000, cues));

        Assert.False(cues[0].HasVideo);
        Assert.False(cues[0].HasAudio);
    }

    [Fact]
    public void A_deleted_scene_never_takes_over_as_a_cue_so_it_cannot_be_fired_and_fixed_in_length()
    {
        LiveCue[] cues = { BlankCue("d", 1000, 2000) };

        Assert.Null(LiveCues.Due(1500, cues, new HashSet<string>()));
        Assert.Empty(LiveCues.ToPrepare(900, 500, cues, new HashSet<string>()));
    }

    // ------------------------------------------------------------------ 6, 7: nothing moves

    [Fact]
    public void A_deleted_scene_leaves_the_timeline_its_length_and_moves_nothing_after_it()
    {
        IngestItem[] items = { Blank(1000, 2000) };

        Assert.Equal(0, IngestTimeline.AddedFrames(items));
        Assert.Equal(5000, IngestTimeline.RulerFrameOf(5000, items));
        Assert.Equal(1500, IngestTimeline.RulerFrameOf(1500, items));
        Assert.Equal(1000, IngestTimeline.RulerStart(items[0], items));

        // The recording is still what runs underneath: same frame, nothing covering it as media.
        TimelineAt at = IngestTimeline.At(1500, items);
        Assert.False(at.OnIngest);
        Assert.Equal(1500, at.CapturedFrame);

        // A segment under it is not cut up the way a cut or an insert would.
        Assert.Single(IngestTimeline.Split(900, 2000, items));
    }

    [Fact]
    public void A_deleted_scene_is_found_on_the_ruler_where_it_was_marked()
    {
        IngestItem[] items = { Blank(1000, 2000) };

        Assert.NotNull(IngestTimeline.BlankAt(1000, items));
        Assert.NotNull(IngestTimeline.BlankAt(1999, items));
        Assert.Null(IngestTimeline.BlankAt(2000, items));
        Assert.Null(IngestTimeline.BlankAt(999, items));
    }

    [Fact]
    public void A_deleted_scene_does_not_change_the_delay()
    {
        LiveCue[] cues = { BlankCue("d", 1000, 2000) };

        Assert.Equal(0, cues[0].DelayChange);
        Assert.Equal(750, LiveCues.DelayWith(750, cues));
    }

    // ------------------------------------------------------------------ 8, 9: no edit zone and conform

    [Fact]
    public void An_edit_reaching_the_no_edit_zone_is_logged_once_and_not_before()
    {
        var log = new EdlLogger();
        var ledger = new EdlLedger(log);
        ledger.Sync(new[] { BlankEdit("d", 1000, 2000) }, "test");

        ledger.NoteZone(air: 700, conformLine: 999);
        Assert.DoesNotContain(log.Entries, e => e.Action == EdlAction.NoEditZone);

        ledger.NoteZone(air: 701, conformLine: 1000);
        ledger.NoteZone(air: 702, conformLine: 1001);

        Assert.Single(log.Entries, e => e.Action == EdlAction.NoEditZone && e.EditId == "d");
    }

    [Fact]
    public void An_edit_is_conformed_when_its_first_frame_reaches_the_line_not_when_its_last_does()
    {
        var log = new EdlLogger();
        var ledger = new EdlLedger(log);
        var playback = new FakePlayback();
        LiveCue[] cues = { BlankCue("d", 1000, 2000) };

        ledger.Sync(new[] { BlankEdit("d", 1000, 2000) }, "test");

        Assert.Empty(ledger.DueForConform(air: 700, conformLine: 999));
        Assert.Single(ledger.DueForConform(air: 750, conformLine: 1000));

        EdlReceipt receipt = ledger.Send(playback, EdlSubmissionKind.Update, cues, Array.Empty<AudioEdit>(), (750, 1000), T0, "test");

        Assert.Equal(EdlVerdict.Accepted, receipt.Verdict);
        EdlRevision sent = Assert.Single(playback.Received);
        Assert.Equal(EdlSubmissionKind.Conform, sent.Kind);
        Assert.Equal(new[] { "d" }, sent.EditIds);

        // The whole intended range is in it, not only what has reached the line.
        LiveCue cue = Assert.Single(sent.Cues);
        Assert.Equal(1000, cue.RecordingFrame);
        Assert.Equal(2000, cue.EndFrame);

        Assert.Equal(EdlConformStatus.Conforming, ledger.Edit("d")!.Conform);
        Assert.Empty(ledger.DueForConform(750, 1000));

        playback.PickUp();
        ledger.Poll(playback, T0.AddMilliseconds(80));

        EdlEditRecord record = ledger.Edit("d")!;
        Assert.Equal(EdlConformStatus.Conformed, record.Conform);
        Assert.Equal(sent.Version, record.ConformedVersion);
        Assert.Equal(sent.TransactionId, record.ConformTransaction);

        Assert.Contains(log.Entries, e => e.Action == EdlAction.ConformStarted && e.EditId == "d" && e.UpdatedVersion == sent.Version);
        Assert.Contains(log.Entries, e => e.Action == EdlAction.PlaybackAcknowledged && e.TransactionId == sent.TransactionId);
    }

    [Fact]
    public void An_edit_already_behind_air_is_history_and_is_not_conformed()
    {
        var ledger = new EdlLedger(new EdlLogger());
        ledger.Sync(new[] { BlankEdit("old", 100, 200) }, "test");

        Assert.Empty(ledger.DueForConform(air: 500, conformLine: 750));
    }

    // ------------------------------------------------------------------ 10, 11, 12: changing it after conform

    private static (EdlLedger Ledger, EdlLogger Log, FakePlayback Playback, EdlRevision First) Conformed(long from, long to)
    {
        var log = new EdlLogger();
        var ledger = new EdlLedger(log);
        var playback = new FakePlayback();

        ledger.Sync(new[] { BlankEdit("d", from, to) }, "test");
        ledger.Send(playback, EdlSubmissionKind.Update, new[] { BlankCue("d", from, to) }, Array.Empty<AudioEdit>(),
                    (from - 250, from), T0, "test");

        playback.PickUp();
        ledger.Poll(playback, T0.AddMilliseconds(40));

        return (ledger, log, playback, playback.Received[^1]);
    }

    [Fact]
    public void Moving_the_out_point_after_conform_reconforms_at_a_new_version_that_supersedes_the_first()
    {
        // Forty seconds deleted at 25 fps, conformed as its first frame reached the line.
        (EdlLedger ledger, EdlLogger log, FakePlayback playback, EdlRevision first) = Conformed(1000, 2000);

        // Before the out point reaches the line, it is moved.
        ledger.Sync(new[] { BlankEdit("d", 1000, 2500) }, "out point moved");

        Assert.Equal(2, ledger.Edit("d")!.Revision);
        Assert.Single(ledger.DueForConform(800, 1050));

        ledger.Send(playback, EdlSubmissionKind.Update, new[] { BlankCue("d", 1000, 2500) }, Array.Empty<AudioEdit>(),
                    (800, 1050), T0.AddSeconds(2), "test");

        EdlRevision second = playback.Received[^1];

        Assert.True(second.Version > first.Version);
        Assert.Equal(EdlSubmissionKind.Conform, second.Kind);
        Assert.Equal(2500, Assert.Single(second.Cues).EndFrame);

        // Logged as a change with the old range and the new, and as a re-conform of the first.
        Assert.Contains(log.Entries, e => e.Action == EdlAction.EditModified && e.EditId == "d" && e.DurationFrames == 1500);
        Assert.Contains(log.Entries, e => e.Action == EdlAction.EdlUpdated && e.PreviousVersion == first.Version && e.UpdatedVersion == second.Version);
        Assert.Contains(log.Entries, e => e.Action == EdlAction.ConformStarted && e.PreviousVersion == first.Version && e.UpdatedVersion == second.Version);
    }

    [Fact]
    public void Extending_a_deleted_scene_blacks_out_the_added_frames_and_only_from_the_old_end()
    {
        LiveCue before = BlankCue("d", 1000, 2000);
        LiveCue after = BlankCue("d", 1000, 2500);

        // Nothing before the old end changes, so an edit whose start is already on air can still grow.
        Assert.Equal(2000, EdlDiff.EarliestChange(before, after));

        Assert.Null(LiveCues.BlankAt(2200, new[] { before }));
        Assert.NotNull(LiveCues.BlankAt(2200, new[] { after }));
        Assert.Null(LiveCues.BlankAt(2500, new[] { after }));

        (EdlLedger ledger, EdlLogger log, _, _) = Conformed(1000, 2000);
        ledger.Sync(new[] { BlankEdit("d", 1000, 2500) }, "extended");

        EdlLogEntry modified = log.Entries.Last(e => e.Action == EdlAction.EditModified);
        Assert.Contains("extended: frames 2,000 to 2,499 now go out black and silent", modified.Instruction);
    }

    [Fact]
    public void Shortening_a_deleted_scene_plays_the_original_frames_it_gives_back()
    {
        LiveCue before = BlankCue("d", 1000, 2000);
        LiveCue after = BlankCue("d", 1000, 1600);

        Assert.Equal(1600, EdlDiff.EarliestChange(before, after));
        Assert.NotNull(LiveCues.BlankAt(1700, new[] { before }));
        Assert.Null(LiveCues.BlankAt(1700, new[] { after }));
        Assert.NotNull(LiveCues.BlankAt(1599, new[] { after }));

        (EdlLedger ledger, EdlLogger log, _, _) = Conformed(1000, 2000);
        ledger.Sync(new[] { BlankEdit("d", 1000, 1600) }, "shortened");

        EdlLogEntry modified = log.Entries.Last(e => e.Action == EdlAction.EditModified);
        Assert.Contains("shortened: the original frames 1,600 to 1,999", modified.Instruction);
        Assert.Equal(600, modified.DurationFrames);
    }

    [Fact]
    public void A_changed_length_on_the_ruler_differs_from_the_nearer_end_but_a_moved_start_from_the_start()
    {
        Assert.True(IngestPlacement.TryPlanBlank(1000, 2000, out IngestPlan a, out _));
        Assert.True(IngestPlacement.TryPlanBlank(1000, 1800, out IngestPlan shorter, out _));
        Assert.True(IngestPlacement.TryPlanBlank(900, 2000, out IngestPlan earlier, out _));

        Assert.Equal(1800, EdlDiff.EarliestChange(a, shorter));
        Assert.Equal(900, EdlDiff.EarliestChange(a, earlier));
        Assert.Equal(1000, EdlDiff.EarliestChange(a, null));
        Assert.Null(EdlDiff.EarliestChange(a, a));
    }

    // ------------------------------------------------------------------ 13: restoring

    [Fact]
    public void Removing_a_deleted_scene_restores_the_original_frames_through_a_newer_edl()
    {
        LiveAir air = OnAir();

        EdlReceipt first = air.Submit(Revision(1, BlankCue("d", 1000, 2000)));
        Assert.Equal(EdlVerdict.Accepted, first.Verdict);
        Assert.NotNull(LiveCues.BlankAt(1500, air.CuesForAir()));

        EdlReceipt restored = air.Submit(Revision(2));
        Assert.Equal(EdlVerdict.Accepted, restored.Verdict);
        Assert.Null(LiveCues.BlankAt(1500, air.CuesForAir()));
        Assert.Equal(2, air.AppliedVersion);

        var log = new EdlLogger();
        var ledger = new EdlLedger(log);
        ledger.Sync(new[] { BlankEdit("d", 1000, 2000) }, "test");
        ledger.Sync(Array.Empty<EdlEditInfo>(), "restored");

        EdlLogEntry gone = Assert.Single(log.Entries, e => e.Action == EdlAction.EditDeleted);
        Assert.Contains("restore the original frames 1,000 to 1,999", gone.Instruction);
    }

    // ------------------------------------------------------------------ 14: versions and duplicates

    [Fact]
    public void Playback_refuses_an_older_version_so_it_cannot_overwrite_a_newer_one()
    {
        LiveAir air = OnAir();

        air.Submit(Revision(5, BlankCue("d", 1000, 2500)));
        EdlReceipt late = air.Submit(Revision(4, BlankCue("d", 1000, 2000)));

        Assert.Equal(EdlVerdict.Stale, late.Verdict);
        Assert.Equal(2500, Assert.Single(air.Cues).EndFrame);
        Assert.Equal(5, air.AcceptedVersion);
    }

    [Fact]
    public void The_same_transaction_twice_is_recognised_and_not_applied_again()
    {
        LiveAir air = OnAir();
        EdlRevision revision = Revision(3, BlankCue("d", 1000, 2000));

        Assert.Equal(EdlVerdict.Accepted, air.Submit(revision).Verdict);

        EdlReceipt again = air.Submit(revision);

        Assert.Equal(EdlVerdict.Duplicate, again.Verdict);
        Assert.True(again.Taken);
        Assert.Equal(3, air.AcceptedVersion);
    }

    [Fact]
    public void Nothing_is_taken_while_not_live()
    {
        var air = new LiveAir();

        Assert.Equal(EdlVerdict.NotLive, air.Submit(Revision(1, BlankCue("d", 1000, 2000))).Verdict);
        Assert.Empty(air.Cues);
    }

    [Fact]
    public void Taken_is_not_applied_until_the_transmitter_reads_it()
    {
        LiveAir air = OnAir();

        air.Submit(Revision(7, BlankCue("d", 1000, 2000)));

        Assert.Equal(7, air.AcceptedVersion);
        Assert.Equal(0, air.AppliedVersion);

        air.CuesForAir();

        Assert.Equal(7, air.AppliedVersion);
    }

    [Fact]
    public void A_change_to_frames_already_sent_is_refused_but_one_ahead_of_air_is_taken()
    {
        LiveAir air = OnAir();
        air.Submit(Revision(1, BlankCue("d", 1000, 2000)));
        air.SetPosition(new LiveAirPosition(1500, null, 0), 750);

        // Moving the start back over frames already gone out: too late.
        EdlReceipt early = air.Submit(Revision(2, BlankCue("d", 900, 2000)));
        Assert.Equal(EdlVerdict.TooLate, early.Verdict);
        Assert.Equal(1000, Assert.Single(air.Cues).RecordingFrame);

        // Lengthening it while it is on air: the change is at frame 2000, still ahead. Taken.
        Assert.Equal(EdlVerdict.Accepted, air.Submit(Revision(3, BlankCue("d", 1000, 2400))).Verdict);

        // Shortening it to before the frame on air would change what has gone out.
        Assert.Equal(EdlVerdict.TooLate, air.Submit(Revision(4, BlankCue("d", 1000, 1400))).Verdict);

        // Shortening it to just ahead of air gives the rest back. Taken.
        Assert.Equal(EdlVerdict.Accepted, air.Submit(Revision(5, BlankCue("d", 1000, 1600))).Verdict);
        Assert.Equal(1600, Assert.Single(air.Cues).EndFrame);
    }

    [Fact]
    public void A_send_is_issued_above_whatever_version_playback_already_holds()
    {
        var ledger = new EdlLedger(new EdlLogger());
        var playback = new FakePlayback();

        // Something else got version 10 in first.
        playback.Submit(Revision(10));

        EdlReceipt receipt = ledger.Send(playback, EdlSubmissionKind.Update, new[] { BlankCue("d", 1000, 2000) },
                                         Array.Empty<AudioEdit>(), null, T0, "test");

        Assert.Equal(EdlVerdict.Accepted, receipt.Verdict);
        Assert.True(receipt.Version > 10);
    }

    // ------------------------------------------------------------------ 15: acknowledgement failures

    [Fact]
    public void An_unacknowledged_revision_is_logged_and_retried_then_retries_stop()
    {
        var log = new EdlLogger();
        var ledger = new EdlLedger(log);
        var playback = new FakePlayback();
        LiveCue[] cues = { BlankCue("d", 1000, 2000) };

        ledger.Send(playback, EdlSubmissionKind.Update, cues, Array.Empty<AudioEdit>(), null, T0, "test");

        Assert.False(ledger.Poll(playback, T0.AddSeconds(1)));         // not late yet

        DateTime now = T0;

        for (int attempt = 1; attempt < EdlLedger.MaxAttempts; attempt++)
        {
            now = now.Add(EdlLedger.AckTimeout).AddMilliseconds(100);
            Assert.True(ledger.Poll(playback, now));                   // retry, please
            Assert.False(ledger.Poll(playback, now.AddMilliseconds(10)));   // said once, not every tick

            ledger.Send(playback, EdlSubmissionKind.Retry, cues, Array.Empty<AudioEdit>(), null, now, "retry");
        }

        now = now.Add(EdlLedger.AckTimeout).AddMilliseconds(100);
        Assert.False(ledger.Poll(playback, now));                      // out of attempts
        Assert.True(ledger.Failing);

        Assert.Equal(EdlLedger.MaxAttempts, log.Entries.Count(e => e.Action == EdlAction.OperationFailed));
        Assert.Equal(EdlLedger.MaxAttempts, playback.Received.Count);

        // Each retry is the latest state at a new version - never a replay of an old one.
        Assert.Equal(playback.Received.Select(r => r.Version).Distinct().Count(), playback.Received.Count);
        Assert.True(playback.Received.Zip(playback.Received.Skip(1)).All(p => p.Second.Version > p.First.Version));

        // A late acknowledgement is still recognised, and clears the failure.
        playback.PickUp();
        ledger.Poll(playback, now.AddSeconds(5));

        Assert.False(ledger.Failing);
        Assert.Null(ledger.Pending);
        Assert.Contains(log.Entries, e => e.Action == EdlAction.PlaybackAcknowledged && e.Message.Contains("late"));
    }

    [Fact]
    public void A_refused_conform_is_logged_as_a_failure_and_the_edit_marked_refused_without_resending_every_tick()
    {
        var log = new EdlLogger();
        var ledger = new EdlLedger(log);
        var playback = new FakePlayback { Refuse = EdlVerdict.TooLate };

        ledger.Sync(new[] { BlankEdit("d", 1000, 2000) }, "test");

        EdlReceipt receipt = ledger.Send(playback, EdlSubmissionKind.Update, new[] { BlankCue("d", 1000, 2000) },
                                         Array.Empty<AudioEdit>(), (750, 1000), T0, "test");

        Assert.False(receipt.Taken);
        Assert.Equal(EdlConformStatus.Rejected, ledger.Edit("d")!.Conform);
        Assert.Empty(ledger.DueForConform(750, 1000));
        Assert.Null(ledger.Pending);

        Assert.Contains(log.Entries, e => e.Action == EdlAction.PlaybackRejected);
        Assert.Contains(log.Entries, e => e.Action == EdlAction.OperationFailed && e.EditId == "d" && e.Error == "refused in test");
    }

    // ------------------------------------------------------------------ 16: the log

    [Fact]
    public void Every_step_of_an_edit_is_logged_from_marks_to_playback()
    {
        string folder = Path.Combine(Path.GetTempPath(), "emerald-edl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            var log = new EdlLogger(folder: () => folder) { LockState = () => "live edit: onair" };
            var ledger = new EdlLedger(log);
            var playback = new FakePlayback();

            EdlSelection range = Selection(1000, 2000, 25, new EdlSegment("a.mp4", 0, 1250), new EdlSegment("b.mp4", 1250, 1250));

            log.Write(new EdlLogEntry { Action = EdlAction.LockChanged, Message = "on air" });
            log.Write(new EdlLogEntry { Action = EdlAction.MarkIn, Message = "in" }.With(range));
            log.Write(new EdlLogEntry { Action = EdlAction.MarkOut, Message = "out" }.With(range));

            ledger.Sync(new[] { new EdlEditInfo("d", IngestMode.Blank, range, "", "1") }, "deleted");
            ledger.Send(playback, EdlSubmissionKind.Update, new[] { BlankCue("d", 1000, 2000) }, Array.Empty<AudioEdit>(), (400, 650), T0, "deleted");
            playback.PickUp();
            ledger.Poll(playback, T0.AddMilliseconds(40));

            ledger.NoteZone(750, 1000);
            ledger.Send(playback, EdlSubmissionKind.Update, new[] { BlankCue("d", 1000, 2000) }, Array.Empty<AudioEdit>(), (750, 1000), T0.AddSeconds(10), "zone");

            ledger.Sync(new[] { new EdlEditInfo("d", IngestMode.Blank, Selection(1000, 2200, 25), "", "2") }, "out moved");
            ledger.Sync(Array.Empty<EdlEditInfo>(), "put back");

            playback.Refuse = EdlVerdict.TooLate;
            ledger.Send(playback, EdlSubmissionKind.Update, Array.Empty<LiveCue>(), Array.Empty<AudioEdit>(), null, T0.AddSeconds(20), "too late");

            HashSet<EdlAction> seen = log.Entries.Select(e => e.Action).ToHashSet();

            foreach (EdlAction action in Enum.GetValues<EdlAction>())
                Assert.Contains(action, seen);

            // Every line can be traced: an operation id each, the edit's id on everything about it.
            Assert.All(log.Entries, e => Assert.False(string.IsNullOrEmpty(e.OperationId)));
            Assert.Equal(log.Entries.Count, log.Entries.Select(e => e.OperationId).Distinct().Count());
            Assert.All(log.Entries, e => Assert.Equal("live edit: onair", e.LockState));

            EdlLogEntry created = log.Entries.Single(e => e.Action == EdlAction.EditCreated);
            Assert.Equal("d", created.EditId);
            Assert.Equal(new[] { "a.mp4", "b.mp4" }, created.SourceFiles);
            Assert.Equal("00:00:40:00", created.MarkIn);
            Assert.Equal("00:01:20:00", created.MarkOut);
            Assert.Equal(1000, created.StartFrame);
            Assert.Equal(1999, created.EndFrame);
            Assert.Equal(1000, created.DurationFrames);
            Assert.Equal(40.0, created.DurationSeconds);
            Assert.Equal(new EdlFrameRun(1000, 1999), Assert.Single(created.SelectedFrames));
            Assert.Equal(2, created.Sources.Count);

            // And the same lines, whole, in the day's EDL file.
            log.Flush(TimeSpan.FromSeconds(5));
            string[] lines = File.ReadAllLines(Path.Combine(folder, $"edl-{created.Timestamp:yyyyMMdd}.jsonl"));

            Assert.Equal(log.Entries.Count, lines.Length);
            Assert.Contains(lines, l => l.Contains("\"action\":\"editCreated\"") && l.Contains("\"sourceFiles\":[\"a.mp4\",\"b.mp4\"]"));
            Assert.Contains(lines, l => l.Contains("\"action\":\"conformStarted\"") && l.Contains("\"transactionId\""));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }
}
