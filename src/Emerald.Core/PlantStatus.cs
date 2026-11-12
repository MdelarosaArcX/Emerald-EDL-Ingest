namespace Emerald.Core;

/// <summary>
/// The strip across the top of both Monitors, worked out once, from everything that can put
/// pictures to air.
///
/// There are three: the playback deck's tidal lock (<see cref="TidalLock"/>), Live Edit's own
/// transmission (<see cref="LiveAir"/>), and the EDL window's queue (written into
/// <see cref="PipelineState"/>). The Monitors used to read only the first and the last, so a
/// plant on air from Live Edit showed TIDAL LOCK, EDL QUEUE and ON AIR as idle. This reads
/// whichever is in use, and the in-app window and the remote page both draw from it, so they
/// cannot disagree with each other either.
/// </summary>
/// <param name="Delay">The TIDAL LOCK card.</param>
/// <param name="Phase">The delay's phase by name - Off, Armed, CountingDown, OnAir, Draining, Failed - whichever of the two transmitters owns it.</param>
/// <param name="Owner">Who holds the delay: "Playback deck", "Live Edit", or "" when nobody does.</param>
/// <param name="TargetFrames">Frames the delay holds back, inserts included. Zero when there is no line.</param>
/// <param name="FrameRate">The line's rate, or 25 when there is none.</param>
/// <param name="Fill">How full the delay is, 0 to 1.</param>
/// <param name="FramesToAir">Frames still to fill before the delay goes to air.</param>
public sealed record PlantStatus(
    StageState Capture,
    StageState Delay,
    StageState Edl,
    StageState OnAir,
    StageState Ingest,
    string Phase,
    string Owner,
    string DelayDetail,
    TimeSpan Behind,
    long TargetFrames,
    int FrameRate,
    double Fill,
    long FramesToAir)
{
    public const string PlaybackDeck = "Playback deck";
    public const string LiveEdit = "Live Edit";

    /// <summary>Whether a delay is open and counting frames out - the frame counts mean something.</summary>
    public bool Transmitting => Phase is nameof(TidalLockState.CountingDown) or nameof(TidalLockState.OnAir)
                                        or nameof(TidalLockState.Draining);

    public static PlantStatus Read(PipelineState state, TidalLock tidal, LiveAir live)
    {
        TidalLockState deck = tidal.State;
        LiveAirState edit = live.State;

        // The deck's lock first: it is the one the playback deck put to air, and Live Edit
        // refuses to go live while it is held. Live Edit only when the deck is not in use.
        bool deckOwns = deck != TidalLockState.Off;
        bool editOwns = !deckOwns && edit != LiveAirState.Off;

        StageState delay;
        string phase, owner, detail;
        TimeSpan behind;
        long target, toAir;
        int rate;
        double fill;

        if (deckOwns)
        {
            owner = PlaybackDeck;
            phase = deck.ToString();
            detail = tidal.Detail;
            behind = tidal.Delay;
            target = tidal.TargetFrames;
            toAir = tidal.FramesToAir;
            fill = tidal.FillFraction;
            rate = tidal.Line?.FrameRate ?? 25;
            delay = DelayStage(phase, fill, $"{owner}: {detail}");
        }
        else if (editOwns)
        {
            owner = LiveEdit;
            phase = edit.ToString();
            detail = live.Detail;
            behind = live.Delay;
            IDelayLine? line = live.Line;
            rate = line?.FrameRate is > 0 and var r ? r : 25;

            // What the transmitter publishes includes every insert and cut on the timeline;
            // before it has said anything, the chosen delay.
            long chosen = line is null ? 0 : (long)Math.Round(live.Delay.TotalSeconds * rate);
            target = live.DelayFrames > 0 ? live.DelayFrames : chosen;
            toAir = line is null ? 0 : Math.Max(0, chosen - line.FramesWritten);
            fill = line is null ? 0 : chosen <= 0 ? 1 : Math.Clamp(line.FramesWritten / (double)chosen, 0, 1);
            delay = DelayStage(phase, fill, $"{owner}: {detail}");
        }
        else
        {
            owner = "";
            phase = nameof(TidalLockState.Off);
            detail = "";
            behind = TimeSpan.Zero;
            target = 0;
            toAir = 0;
            fill = 0;
            rate = 25;
            delay = StageState.Idle;
        }

        return new PlantStatus(
            Capture: state.Capture,
            Delay: delay,
            Edl: state.Edl != StageState.Idle ? state.Edl
                 : editOwns ? LiveEditQueue(state, live)
                 : StageState.Idle,
            OnAir: state.OnAir != StageState.Idle ? state.OnAir
                   : deckOwns ? DeckOnAir(deck, state)
                   : editOwns ? LiveEditOnAir(edit, state, live)
                   : StageState.Idle,
            Ingest: state.Ingest,
            Phase: phase,
            Owner: owner,
            DelayDetail: detail,
            Behind: behind,
            TargetFrames: target,
            FrameRate: rate,
            Fill: fill,
            FramesToAir: toAir);
    }

    /// <summary>The TIDAL LOCK card for a phase, the same words whichever transmitter it is.</summary>
    internal static StageState DelayStage(string phase, double fill, string detail) => phase switch
    {
        nameof(TidalLockState.Off) => StageState.Idle,
        nameof(TidalLockState.Armed) => new StageState("ARMED", detail, LogLevel.Warn),
        nameof(TidalLockState.CountingDown) => new StageState($"FILLING {fill * 100:0}%", detail, LogLevel.Warn),
        nameof(TidalLockState.OnAir) => new StageState("ON AIR", detail, LogLevel.Ok),
        nameof(TidalLockState.Draining) => new StageState("DRAINING", detail, LogLevel.Warn),
        _ => new StageState("FAILED", detail, LogLevel.Error),
    };

    /// <summary>
    /// Live Edit's EDL: the revision held, how many edits are still to come, and the next one.
    /// Every edit on Live Edit's timeline is an entry of the EDL it sends; this is that queue.
    /// </summary>
    internal static StageState LiveEditQueue(PipelineState state, LiveAir live)
    {
        IReadOnlyList<LiveCue> cues = live.Cues;
        long version = live.AcceptedVersion;
        long applied = live.AppliedVersion;

        if (version == 0 && cues.Count == 0)
            return new StageState("no edits", "Live Edit: nothing on the timeline yet", LogLevel.Info);

        // Still to come: anything starting after the frame going out now (all of them before
        // the transmitter has said where it is).
        long at = live.Position?.RecordingFrame ?? -1;
        LiveCue[] ahead = cues.Where(c => c.RecordingFrame > at).OrderBy(c => c.RecordingFrame).ToArray();

        string next = ahead.Length == 0 ? ""
            : $"  ·  next {ahead[0].Name} at " +
              (PipelineState.TimecodeOf(state.RecordingStart, ahead[0].RecordingFrame)?.ToString()
               ?? $"frame {ahead[0].RecordingFrame:N0}");

        bool waiting = applied < version;

        return new StageState(
            ahead.Length == 0 ? $"EDL v{version}" : $"{ahead.Length} to come",
            $"Live Edit EDL v{version}, {cues.Count} edit{(cues.Count == 1 ? "" : "s")}" +
            (waiting ? $" (on air: v{applied})" : "") + next,
            waiting ? LogLevel.Warn : LogLevel.Info);
    }

    /// <summary>What Live Edit is putting out right now.</summary>
    internal static StageState LiveEditOnAir(LiveAirState edit, PipelineState state, LiveAir live)
    {
        switch (edit)
        {
            case LiveAirState.Armed:
                return new StageState("WAITING", "Live Edit: waiting for the capture deck to record", LogLevel.Warn);

            case LiveAirState.CountingDown:
                return new StageState("BLACK", "Live Edit: black until the delay is full", LogLevel.Warn);

            case LiveAirState.Failed:
                return new StageState("FAILED", $"Live Edit: {live.Detail}", LogLevel.Error);

            case LiveAirState.OnAir:
            case LiveAirState.Draining:
                string what = live.Position is { CueId: { } id } && live.Cues.FirstOrDefault(c => c.Id == id) is { } cue
                    ? cue.Name
                    : $"the recording, {TidalLock.Describe(live.Delay)} behind";

                return new StageState(
                    edit == LiveAirState.OnAir ? "ON AIR" : "DRAINING",
                    $"Live Edit: {what}{TakenAt(state)}",
                    edit == LiveAirState.OnAir ? LogLevel.Ok : LogLevel.Warn);

            default:
                return StageState.Idle;
        }
    }

    /// <summary>The playback deck's delayed recording, when the EDL window is not saying more.</summary>
    internal static StageState DeckOnAir(TidalLockState deck, PipelineState state) => deck switch
    {
        TidalLockState.OnAir => new StageState("ON AIR", $"{PlaybackDeck}: the delayed recording{TakenAt(state)}", LogLevel.Ok),
        TidalLockState.Draining => new StageState("DRAINING", $"{PlaybackDeck}: the last of the delay{TakenAt(state)}", LogLevel.Warn),
        TidalLockState.CountingDown => new StageState("BLACK", $"{PlaybackDeck}: black until the delay is full", LogLevel.Warn),
        _ => StageState.Idle,
    };

    private static string TakenAt(PipelineState state) =>
        state.OnAirTimeOfDay is { } t ? $", taken at {t:hh\\:mm\\:ss}" : "";
}
