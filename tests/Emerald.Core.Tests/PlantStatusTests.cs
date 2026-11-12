using System.Text.Json;
using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// The Monitors' strip, from whichever transmitter is in use. Live Edit going live used to
/// leave TIDAL LOCK, EDL QUEUE and ON AIR reading idle, because only the playback deck's lock
/// and the EDL window were read.
/// </summary>
public sealed class PlantStatusTests
{
    private sealed class Line : IDelayLine
    {
        public int Width => 1920;
        public int Height => 1080;
        public int FrameRate => 25;
        public long FramesWritten { get; set; }
        public long TargetFrames => 0;
        public bool IsSealed => false;
        public long SealedAt => 0;
        public string Path => "test";
    }

    private static EdlRevision Revision(long version, params LiveCue[] cues) =>
        new(version, Guid.NewGuid().ToString("N"), EdlSubmissionKind.Update, cues, Array.Empty<AudioEdit>(),
            DateTime.UtcNow, Array.Empty<string>());

    private static LiveCue Insert(string id, long at) =>
        new(id, IngestMode.Insert, $@"C:\media\{id}.mxf", at, Frames: 250, Covers: 0);

    [Fact]
    public void Nothing_in_use_is_idle_everywhere()
    {
        PlantStatus plant = PlantStatus.Read(new PipelineState(), new TidalLock(), new LiveAir());

        Assert.Equal(StageState.Idle, plant.Delay);
        Assert.Equal(StageState.Idle, plant.Edl);
        Assert.Equal(StageState.Idle, plant.OnAir);
        Assert.Equal("Off", plant.Phase);
        Assert.False(plant.Transmitting);
    }

    [Fact]
    public void Live_edit_filling_shows_on_the_tidal_lock_card()
    {
        var air = new LiveAir();
        air.Arm(TimeSpan.FromSeconds(10), TimeSpan.Zero, 0, new[] { 1 });

        PlantStatus armed = PlantStatus.Read(new PipelineState(), new TidalLock(), air);
        Assert.Equal("ARMED", armed.Delay.Headline);
        Assert.Equal(PlantStatus.LiveEdit, armed.Owner);
        Assert.Equal("WAITING", armed.OnAir.Headline);

        var line = new Line { FramesWritten = 125 };   // 5 s of a 10 s delay
        air.RecordingRolled(line, DateTime.Today);

        PlantStatus filling = PlantStatus.Read(new PipelineState(), new TidalLock(), air);
        Assert.Equal("FILLING 50%", filling.Delay.Headline);
        Assert.StartsWith("Live Edit:", filling.Delay.Detail);
        Assert.Equal(125, filling.FramesToAir);
        Assert.Equal(250, filling.TargetFrames);
        Assert.Equal("BLACK", filling.OnAir.Headline);
        Assert.True(filling.Transmitting);
    }

    [Fact]
    public void Live_edit_on_air_fills_the_edl_and_on_air_cards()
    {
        var air = new LiveAir();
        air.Arm(TimeSpan.FromSeconds(10), TimeSpan.Zero, 0, new[] { 1 });
        air.RecordingRolled(new Line { FramesWritten = 250 }, DateTime.Today);
        air.WentToAir();

        PlantStatus empty = PlantStatus.Read(new PipelineState(), new TidalLock(), air);
        Assert.Equal("ON AIR", empty.Delay.Headline);
        Assert.Equal("ON AIR", empty.OnAir.Headline);
        Assert.Contains("the recording", empty.OnAir.Detail);
        Assert.Equal("no edits", empty.Edl.Headline);

        Assert.Equal(EdlVerdict.Accepted, air.Submit(Revision(1, Insert("a", 100), Insert("b", 900))).Verdict);
        air.SetPosition(new LiveAirPosition(500, null, 0), 300);

        PlantStatus queued = PlantStatus.Read(new PipelineState(), new TidalLock(), air);
        Assert.Equal("1 to come", queued.Edl.Headline);
        Assert.Contains("v1", queued.Edl.Detail);
        Assert.Contains("next b.mxf", queued.Edl.Detail);
        Assert.Contains("(on air: v0)", queued.Edl.Detail);   // not picked up by the transmitter yet
        Assert.Equal(LogLevel.Warn, queued.Edl.Level);
        Assert.Equal(300, queued.TargetFrames);                // what the transmitter published

        air.CuesForAir();
        air.SetPosition(new LiveAirPosition(950, "b", 50), 300);

        PlantStatus playing = PlantStatus.Read(new PipelineState(), new TidalLock(), air);
        Assert.Equal(LogLevel.Info, playing.Edl.Level);
        Assert.Contains("b.mxf", playing.OnAir.Detail);
    }

    [Fact]
    public void The_edl_window_and_the_playback_deck_still_take_precedence()
    {
        var state = new PipelineState
        {
            Edl = new StageState("2 waiting", "next X", LogLevel.Info),
            OnAir = new StageState("ON AIR", "X TX1 clip", LogLevel.Ok),
        };
        var tidal = new TidalLock();
        tidal.Arm(TimeSpan.FromSeconds(10));

        PlantStatus plant = PlantStatus.Read(state, tidal, new LiveAir());

        Assert.Equal("2 waiting", plant.Edl.Headline);
        Assert.Equal("X TX1 clip", plant.OnAir.Detail);
        Assert.Equal("ARMED", plant.Delay.Headline);
        Assert.Equal(PlantStatus.PlaybackDeck, plant.Owner);
    }

    [Fact]
    public void The_remote_page_gets_the_same_cards()
    {
        var air = new LiveAir();
        air.Arm(TimeSpan.FromSeconds(10), TimeSpan.Zero, 0, new[] { 1 });

        using var log = new ActivityLog();
        string json = MonitorFeed.State(log, new PipelineState(), new TidalLock(), () => null, air);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement delay = doc.RootElement.GetProperty("delay");
        Assert.Equal("Armed", delay.GetProperty("phase").GetString());
        Assert.Equal("Live Edit", delay.GetProperty("owner").GetString());
        Assert.Equal("ARMED", delay.GetProperty("stage").GetProperty("headline").GetString());
        Assert.Equal("WAITING", doc.RootElement.GetProperty("onAir").GetProperty("headline").GetString());
    }

    [Fact]
    public void Live_edit_cues_are_tallied_per_go_live()
    {
        var tally = new TransmissionTally();
        DateTimeOffset t = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        long seq = 0;

        LogLine L(string @event, string? file = null, int seconds = 0) =>
            new(++seq, t.AddSeconds(seconds), null, LogSource.LiveEdit, LogLevel.Ok, "x",
                @event, Correlation: "cue-1", File: file);

        tally.Add(L("liveair.armed"));
        tally.Add(L("liveair.cue.prepared", @"C:\m\a.mxf", 5));
        tally.Add(L("liveair.cue.prepared", @"C:\m\b.mxf", 6));
        tally.Add(L("liveair.cue.playing", @"C:\m\a.mxf", 10));
        tally.Add(L("liveair.cue.playing", @"C:\m\a.mxf", 10));   // the same line twice

        var rows = tally.Rows();
        Assert.Single(rows);
        Assert.StartsWith("Live Edit on air from", rows[0].Edl.Headline);

        TransmittedFile a = rows[0].Files.Single(f => f.FileName == "a.mxf");
        TransmittedFile b = rows[0].Files.Single(f => f.FileName == "b.mxf");
        Assert.Equal(1, a.Times);
        Assert.False(b.Aired);

        // A second go-live is a second heading.
        tally.Add(L("liveair.armed", seconds: 600));
        tally.Add(L("liveair.cue.playing", @"C:\m\c.mxf", 610));
        Assert.Equal(2, tally.Rows().Count);
    }
}
