using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// What went to air, per message.
///
/// The point of folding this out of the record rather than keeping a second set of books is
/// that it cannot disagree with the record. These are the properties that has to hold: the
/// same line twice is one transmission, picture and every language are separate rows, and a
/// file that was booked but never reached the transmitter reads as queued rather than as aired.
/// </summary>
public class TransmissionTallyTests
{
    private static long _seq;

    private static LogLine Line(string @event, string? correlation = null, string? file = null,
                                string? detail = null, string message = "", DateTimeOffset? at = null) =>
        new(Interlocked.Increment(ref _seq), at ?? DateTimeOffset.Now, null,
            LogSource.Playout, LogLevel.Info, message, @event, correlation, file, detail);

    private static string AudioDetail(int track, string label) =>
        $"track {track}\nlabel {label}\nchannels {track * 2 - 1}-{track * 2}";

    [Fact]
    public void A_message_with_a_picture_and_two_languages_is_three_rows()
    {
        var tally = new TransmissionTally();

        tally.Add(Line("edl.queued", "a1b2c3d4", message: "EDL a1b2c3d4 queued"));
        tally.Add(Line("edl.video", "a1b2c3d4", @"C:\m\promo.mov"));
        tally.Add(Line("edl.audio", "a1b2c3d4", @"C:\m\en.wav", AudioDetail(1, "English")));
        tally.Add(Line("edl.audio", "a1b2c3d4", @"C:\m\ar.wav", AudioDetail(2, "Arabic")));

        IReadOnlyList<TransmittedFile> files = tally.Flat();

        Assert.Equal(3, files.Count);
        Assert.Equal(new[] { "VIDEO", "AUDIO 1", "AUDIO 2" }, files.Select(f => f.Kind));
        Assert.Equal(new[] { "", "English", "Arabic" }, files.Select(f => f.Label));
        Assert.Equal("3-4", files[2].Channels);
    }

    [Fact]
    public void A_file_that_was_booked_but_never_aired_says_so()
    {
        var tally = new TransmissionTally();

        tally.Add(Line("edl.video", "a1b2c3d4", @"C:\m\promo.mov"));

        TransmittedFile file = tally.Flat().Single();

        Assert.False(file.Aired);
        Assert.Equal("QUEUED", file.State);
        Assert.Equal(0, file.Times);
        Assert.Null(file.FirstAired);
    }

    [Fact]
    public void Reaching_the_transmitter_is_what_makes_it_aired()
    {
        var tally = new TransmissionTally();
        var at = DateTimeOffset.Parse("2026-09-18T14:30:00+10:00");

        tally.Add(Line("edl.video", "a1b2c3d4", @"C:\m\promo.mov"));
        tally.Add(Line("playout.video", "a1b2c3d4", @"C:\m\promo.mov", at: at));

        TransmittedFile file = tally.Flat().Single();

        Assert.True(file.Aired);
        Assert.Equal("AIRED", file.State);
        Assert.Equal(1, file.Times);
        Assert.Equal(at, file.FirstAired);
    }

    /// <summary>
    /// The page folds today's file in and then replays the ring, which still holds the newest
    /// of those same lines. Counting a transmission twice because it was read twice would make
    /// the number wrong in exactly the case the file was loaded to get right.
    /// </summary>
    [Fact]
    public void The_same_line_read_twice_is_one_transmission()
    {
        var tally = new TransmissionTally();
        LogLine aired = Line("playout.video", "a1b2c3d4", @"C:\m\promo.mov");

        tally.Add(aired);
        tally.Add(aired);

        Assert.Equal(1, tally.Flat().Single().Times);
    }

    /// <summary>A bed looping through its files puts each of them to air, and each is counted.</summary>
    [Fact]
    public void A_track_that_loops_counts_every_time_it_comes_round()
    {
        var tally = new TransmissionTally();
        string path = @"C:\m\en.wav";

        for (int i = 0; i < 3; i++)
            tally.Add(Line("playout.audio", "a1b2c3d4", path, AudioDetail(1, "English"),
                           at: DateTimeOffset.Now.AddMinutes(i)));

        Assert.Equal(3, tally.Flat().Single().Times);
    }

    /// <summary>
    /// Eight beds roll at their own pace, so their lines arrive interleaved and in no
    /// particular order. The track has to come from the line, not from where it landed.
    /// </summary>
    [Fact]
    public void Tracks_are_told_apart_by_what_the_line_says_not_by_the_order_they_arrive()
    {
        var tally = new TransmissionTally();

        tally.Add(Line("playout.audio", "a1b2c3d4", @"C:\m\ar.wav", AudioDetail(3, "Arabic")));
        tally.Add(Line("playout.audio", "a1b2c3d4", @"C:\m\en.wav", AudioDetail(1, "English")));

        Assert.Equal(new[] { "AUDIO 1", "AUDIO 3" }, tally.Flat().Select(f => f.Kind));
    }

    [Fact]
    public void The_same_clip_under_two_messages_is_two_rows()
    {
        var tally = new TransmissionTally();

        tally.Add(Line("playout.video", "aaaaaaaa", @"C:\m\promo.mov"));
        tally.Add(Line("playout.video", "bbbbbbbb", @"C:\m\promo.mov"));

        Assert.Equal(2, tally.FileCount);
        Assert.Equal(2, tally.EdlCount);
    }

    /// <summary>
    /// The playback deck puts a clip straight to air without going through the EDL, so there
    /// is no command id on the line. It still went out, and still has to appear.
    /// </summary>
    [Fact]
    public void Something_aired_outside_an_EDL_still_gets_a_heading()
    {
        var tally = new TransmissionTally();

        tally.Add(Line("playout.video", null, @"C:\m\promo.mov"));

        (TransmittedEdl edl, IReadOnlyList<TransmittedFile> files) = tally.Rows().Single();

        Assert.Equal("", edl.EdlId);
        Assert.Equal("Not under an EDL", edl.Headline);
        Assert.True(files.Single().Aired);
    }

    [Fact]
    public void Lines_that_are_not_about_media_are_ignored()
    {
        var tally = new TransmissionTally();

        tally.Add(Line("capture.started", "55b11c10"));
        tally.Add(Line("delay.onair"));
        tally.Add(Line(null!));

        Assert.Equal(0, tally.FileCount);
    }

    /// <summary>A media line with no file on it names nothing and must not create an empty row.</summary>
    [Fact]
    public void A_media_line_with_no_file_creates_nothing()
    {
        var tally = new TransmissionTally();

        tally.Add(Line("playout.video", "a1b2c3d4"));
        tally.Add(Line("edl.audio", "a1b2c3d4", detail: AudioDetail(1, "English")));

        Assert.Equal(0, tally.FileCount);
    }

    [Fact]
    public void Messages_come_back_in_the_order_they_first_appeared_picture_before_sound()
    {
        var tally = new TransmissionTally();

        tally.Add(Line("edl.queued", "first", message: "EDL first queued"));
        tally.Add(Line("edl.audio", "first", @"C:\m\en.wav", AudioDetail(1, "English")));
        tally.Add(Line("edl.video", "first", @"C:\m\a.mov"));
        tally.Add(Line("edl.queued", "second", message: "EDL second queued"));
        tally.Add(Line("edl.video", "second", @"C:\m\b.mov"));

        var rows = tally.Rows();

        Assert.Equal(new[] { "first", "second" }, rows.Select(r => r.Edl.EdlId));
        Assert.Equal(new[] { "VIDEO", "AUDIO 1" }, rows[0].Files.Select(f => f.Kind));
        Assert.Equal("EDL first queued", rows[0].Edl.Headline);
    }
}

public class TransmissionTallyScriptTests
{
    private static LogLine Line(string @event, string? file) =>
        new(1, DateTimeOffset.Now, null, LogSource.Playout, LogLevel.Info, "", @event, "a1b2c3d4", file);

    /// <summary>
    /// The script writes a PLAY line and an END line for every file, both naming it. Only the
    /// first is the file reaching air; counting the second would double every transmission.
    /// </summary>
    [Fact]
    public void A_file_ending_is_not_a_second_transmission()
    {
        var tally = new TransmissionTally();

        tally.Add(Line("playout.video", @"C:\m\promo.mov"));
        tally.Add(Line("playout.videoend", @"C:\m\promo.mov"));

        Assert.Equal(1, tally.Flat().Single().Times);
    }
}
