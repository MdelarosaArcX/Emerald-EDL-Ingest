using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// The per-recording folder and the segment numbering.
///
/// This decides what a plant's recordings are called, so getting it wrong is either files that
/// overwrite each other or a numbering nobody can reason about. The properties that matter: every
/// recording gets its own folder, the numbering starts again inside it, and the moment it rolled
/// is on the face of every file.
/// </summary>
public class RecordingSequenceTests
{
    private static readonly DateTime Evening = new(2026, 9, 30, 22, 9, 14);

    [Fact]
    public void The_folder_is_the_moment_record_was_pressed()
    {
        Assert.Equal("2026-09-30_22-09-14", RecordingSequence.Next(Evening, "media").Folder);
    }

    [Fact]
    public void The_file_is_the_prefix_the_date_the_time_and_the_number()
    {
        RecordingNames names = RecordingSequence.Next(Evening, "media");

        Assert.Equal("media_0930262209_%d.mp4", names.PatternFor("mp4"));
        Assert.Equal("media_0930262209_1.mp4", names.FirstFile("mp4"));
        Assert.Equal("media_0930262209_1.mov", names.FirstFile("mov"));
    }

    /// <summary>
    /// The thing that changed, and the reason it is simpler: a number only has to be unique
    /// inside its own folder, so nothing is carried between recordings and nothing can be left
    /// behind by a crash or thrown backwards when the storage limit prunes the oldest recording.
    /// </summary>
    [Fact]
    public void Every_recording_starts_its_numbering_again_at_one()
    {
        Assert.Equal(1, RecordingSequence.Next(Evening, "media").Start);
        Assert.Equal(1, RecordingSequence.Next(Evening.AddHours(1), "media").Start);
        Assert.Equal(1, RecordingSequence.Next(Evening.AddYears(1), "media").Start);
        Assert.Equal(RecordingSequence.FirstNumber, RecordingSequence.Next(Evening, "media").Start);
    }

    /// <summary>Two recordings a minute apart are two folders, so their files cannot collide.</summary>
    [Fact]
    public void Two_recordings_get_two_folders_and_two_sets_of_names()
    {
        RecordingNames first = RecordingSequence.Next(Evening, "media");
        RecordingNames second = RecordingSequence.Next(Evening.AddMinutes(1), "media");

        Assert.NotEqual(first.Folder, second.Folder);
        Assert.NotEqual(first.FirstFile("mp4"), second.FirstFile("mp4"));

        Assert.Equal("media_0930262209_1.mp4", first.FirstFile("mp4"));
        Assert.Equal("media_0930262210_1.mp4", second.FirstFile("mp4"));
    }

    /// <summary>
    /// The number is unpadded and unbounded, so nothing reading these names may assume a width.
    /// </summary>
    [Theory]
    [InlineData("media_0930262209_1.mp4", 1)]
    [InlineData("media_0930262209_12.mp4", 12)]
    [InlineData("media_0930262209_1000000.mp4", 1_000_000)]
    [InlineData("media_0930262209_0.mov", 0)]
    [InlineData("my_show_2_0930262209_4200.mp4", 4200)]     // a prefix with its own digits
    public void The_number_is_read_off_the_end_of_the_name(string file, long expected)
    {
        Assert.Equal(expected, RecordingSequence.NumberIn(file));
    }

    [Theory]
    [InlineData("capture_2026-09-30_10-07-33.mp4")]          // the old flat scheme
    [InlineData("media.mp4")]
    [InlineData("media_.mp4")]
    [InlineData("")]
    public void A_name_from_another_scheme_has_no_number(string file)
    {
        Assert.Null(RecordingSequence.NumberIn(file));
    }

    [Fact]
    public void A_folder_says_when_it_was_made_and_anything_else_says_nothing()
    {
        Assert.Equal(Evening, RecordingSequence.FolderStartedAt("2026-09-30_22-09-14"));
        Assert.Null(RecordingSequence.FolderStartedAt("low"));
        Assert.Null(RecordingSequence.FolderStartedAt("2026-09-30"));
    }

    /// <summary>Folder names sort chronologically as text, which is what orders the recordings.</summary>
    [Fact]
    public void Folders_sort_into_the_order_the_recordings_happened()
    {
        string[] shuffled =
        {
            RecordingSequence.Next(Evening.AddDays(1), "media").Folder,
            RecordingSequence.Next(Evening, "media").Folder,
            RecordingSequence.Next(Evening.AddHours(2), "media").Folder,
        };

        Assert.Equal(
            new[]
            {
                RecordingSequence.Next(Evening, "media").Folder,
                RecordingSequence.Next(Evening.AddHours(2), "media").Folder,
                RecordingSequence.Next(Evening.AddDays(1), "media").Folder,
            },
            shuffled.OrderBy(f => f, StringComparer.Ordinal));
    }

    // ------------------------------------------------------------------ midnight

    /// <summary>
    /// A recording rolled at 23:30:00 hands over to the new day's folder after exactly half an
    /// hour of frames, so the folder can be named 00-00-00 and be right to the frame.
    /// </summary>
    [Fact]
    public void Midnight_is_an_exact_count_of_frames_from_the_folder_start()
    {
        var rolled = new DateTime(2026, 10, 17, 23, 30, 0);

        Assert.Equal(30 * 60 * 25, RecordingSequence.FramesToNextDay(rolled, 25));
        Assert.Equal(30 * 60 * 50, RecordingSequence.FramesToNextDay(rolled, 50));
    }

    [Fact]
    public void A_recording_rolled_at_midnight_has_a_whole_day_to_run()
    {
        var midnight = new DateTime(2026, 10, 18);

        Assert.Equal(24L * 3600 * 25, RecordingSequence.FramesToNextDay(midnight, 25));
    }

    /// <summary>The new day's folder is named for midnight, so Live Edit places it at 00:00:00.</summary>
    [Fact]
    public void The_new_day_folder_reads_back_as_midnight()
    {
        var midnight = new DateTime(2026, 10, 18);
        RecordingNames names = RecordingSequence.Next(midnight, "media");

        Assert.Equal("2026-10-18_00-00-00", names.Folder);
        Assert.Equal(midnight, RecordingSequence.FolderStartedAt(names.Folder));
        Assert.Equal(1, names.Start);
    }
}

/// <summary>
/// The default name a recording's files take.
///
/// It was five literals in five files, which is how it came to be "capture" in some of them and
/// "media" in others. One constant now, and the old default is recognised so a settings file
/// still carrying it is not mistaken for a title somebody chose.
/// </summary>
public class RecordingPrefixTests
{
    [Fact]
    public void An_unnamed_recording_is_called_media()
    {
        Assert.Equal("media", RecordingSequence.DefaultPrefix);
        Assert.Equal("media", RecordingSequence.PrefixOr(null));
        Assert.Equal("media", RecordingSequence.PrefixOr(""));
        Assert.Equal("media", RecordingSequence.PrefixOr("   "));
    }

    /// <summary>
    /// The old default was written into the settings the first time the deck ever opened, so it
    /// is not a choice — and left alone, every machine that has run Emerald would keep writing
    /// files under the old name for ever.
    /// </summary>
    [Theory]
    [InlineData("capture")]
    [InlineData("Capture")]
    [InlineData("  capture  ")]
    public void A_settings_file_still_holding_the_old_default_gets_the_new_one(string stored)
    {
        Assert.Equal("media", RecordingSequence.PrefixOr(stored));
    }

    [Theory]
    [InlineData("news", "news")]
    [InlineData("  Evening Bulletin  ", "Evening Bulletin")]
    [InlineData("capture2", "capture2")]              // not the old default, so it is a choice
    public void A_title_somebody_typed_is_left_exactly_as_it_is(string typed, string expected)
    {
        Assert.Equal(expected, RecordingSequence.PrefixOr(typed));
    }

    [Fact]
    public void The_default_runs_all_the_way_through_to_the_filename()
    {
        RecordingNames names = RecordingSequence.Next(new DateTime(2026, 9, 30, 22, 9, 14), "");

        Assert.Equal("media_0930262209_1.mp4", names.FirstFile("mp4"));
    }
}
