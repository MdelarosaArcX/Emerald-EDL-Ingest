using System.IO;
using Emerald.Core;
using Emerald.Video;
using Xunit;

namespace Emerald.Ingest.Tests;

/// <summary>
/// Extra audio on an ingest.
///
/// The receiver's own sound is not one of these: it is always recorded and always the first
/// track in the file. Everything here is about what gets recorded <em>alongside</em> it — the
/// language beds and commentaries an operator picks — and about refusing a selection before a
/// receiver is opened rather than discovering it when the encoder is already running.
/// </summary>
public sealed class IngestAudioTests : IDisposable
{
    private readonly string _directory;
    private readonly FakeClock _clock = new("20:00:00:00");
    private readonly InMemoryStore _store = new();
    private readonly IngestControllerService _controller;

    public IngestAudioTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "emerald-ingest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _controller = new IngestControllerService(
            new AppSettings(), _clock, new MockIngestHardware(),
            store: _store, recorderFactory: () => new StubRecorder(), registrar: new StubRegistrar());
    }

    private IngestRequest Good() => new()
    {
        BoardIndex = 0,
        BoardName = "DELTA-3G-elp-h-22",
        Port = "RX0",
        PortIndex = 0,
        FrameRate = 25,
        ReferenceTimecode = "20:57:26:00",
        Som = "00:01:00:00",
        Duration = "00:00:10:00",
        TimingMode = IngestTimingMode.DurationControlsEom,
        ClipName = "CLIP_TEST_0001",
        Directory = _directory,
    };

    /// <summary>An audio file that exists. Its contents never matter here — only its path.</summary>
    private string MakeAudio(string name)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, new byte[64]);
        return path;
    }

    [Fact]
    public void An_ingest_with_no_extra_audio_is_still_perfectly_valid()
    {
        IngestValidation v = _controller.Validate(Good());

        Assert.True(v.IsValid, string.Join("; ", v.Messages));
        Assert.Empty(v.Job!.AudioTracks);
    }

    [Fact]
    public void The_selected_tracks_are_carried_onto_the_job_in_the_order_they_were_picked()
    {
        IngestRequest request = Good() with
        {
            AudioTracks = new[]
            {
                new CaptureAudioTrack("English", MakeAudio("en.wav")),
                new CaptureAudioTrack("Arabic", MakeAudio("ar.wav")),
            },
        };

        IngestJob job = _controller.Validate(request).Job!;

        Assert.Collection(job.AudioTracks,
            t => Assert.Equal("English", t.Label),
            t => Assert.Equal("Arabic", t.Label));
    }

    [Fact]
    public void A_track_whose_file_is_not_there_is_refused_before_anything_is_opened()
    {
        IngestRequest request = Good() with
        {
            AudioTracks = new[]
            {
                new CaptureAudioTrack("Missing", Path.Combine(_directory, "not-here.wav")),
            },
        };

        IngestValidation v = _controller.Validate(request);

        Assert.False(v.IsValid);
        Assert.NotNull(v.For(IngestFields.Audio));
        Assert.Null(v.Job);
    }

    [Fact]
    public void More_tracks_than_the_embedder_can_carry_are_refused()
    {
        var tracks = new List<CaptureAudioTrack>();
        for (int i = 0; i <= IngestControllerService.MaxAudioTracks; i++)
            tracks.Add(new CaptureAudioTrack($"Track {i}", MakeAudio($"a{i}.wav")));

        IngestValidation v = _controller.Validate(Good() with { AudioTracks = tracks });

        Assert.False(v.IsValid);
        Assert.NotNull(v.For(IngestFields.Audio));
    }

    [Fact]
    public void Exactly_the_maximum_is_allowed()
    {
        var tracks = new List<CaptureAudioTrack>();
        for (int i = 0; i < IngestControllerService.MaxAudioTracks; i++)
            tracks.Add(new CaptureAudioTrack($"Track {i}", MakeAudio($"b{i}.wav")));

        IngestValidation v = _controller.Validate(Good() with { AudioTracks = tracks });

        Assert.True(v.IsValid, string.Join("; ", v.Messages));
        Assert.Equal(IngestControllerService.MaxAudioTracks, v.Job!.AudioTracks.Count);
    }

    [Fact]
    public void The_tracks_survive_the_round_trip_through_the_store()
    {
        var picked = new CaptureAudioTrack("Commentary", MakeAudio("comm.wav"));

        // What persistence actually stores is the JSON; the list is a view over it. Going in
        // and back out through that column is the trip a recovered job makes after a crash.
        var job = new IngestJob { AudioTracks = new[] { picked } };
        var recovered = new IngestJob { AudioTracksJson = job.AudioTracksJson };

        Assert.Equal(picked, Assert.Single(recovered.AudioTracks));
    }

    [Fact]
    public void A_job_with_nothing_selected_stores_nothing_rather_than_an_empty_array()
    {
        var job = new IngestJob { AudioTracks = Array.Empty<CaptureAudioTrack>() };

        Assert.Equal("", job.AudioTracksJson);
        Assert.Empty(job.AudioTracks);
    }

    [Fact]
    public void A_column_holding_something_that_is_not_a_track_list_reads_as_no_tracks()
    {
        // Never throw on the way out of the database: a job recovered from a older or
        // hand-edited row must still be listable, just without its audio.
        var job = new IngestJob { AudioTracksJson = "{ not json" };

        Assert.Empty(job.AudioTracks);
    }

    [Fact]
    public void A_database_written_before_audio_tracks_existed_still_opens()
    {
        // The real one is a user's ingest.db from a build that had no audio column. Standing
        // one up means building the current schema and taking the column back out again -
        // EnsureCreated would leave such a file untouched, and every query against Jobs would
        // then fail on the missing column.
        string database = Path.Combine(_directory, "old.db");

        var before = new SqliteIngestStore(database);
        before.Initialise();
        before.Save(new IngestJob { ClipName = "CLIP_OLD_0001", FrameRate = 25 });

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            using Microsoft.Data.Sqlite.SqliteCommand drop = connection.CreateCommand();
            drop.CommandText = "ALTER TABLE \"Jobs\" DROP COLUMN \"AudioTracksJson\"";
            drop.ExecuteNonQuery();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var after = new SqliteIngestStore(database);
        after.Initialise();

        IngestJob recovered = Assert.Single(after.LoadUnfinished());
        Assert.Equal("CLIP_OLD_0001", recovered.ClipName);
        Assert.Empty(recovered.AudioTracks);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
