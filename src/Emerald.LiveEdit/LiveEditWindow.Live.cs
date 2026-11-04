using System.Windows;
using System.Windows.Media;
using Emerald.Core;
using Emerald.Media;
using Emerald.Video;

namespace Emerald.LiveEdit;

/// <summary>
/// Going live from Live Edit: the transmitter, the EDL the timeline writes for it, the delay
/// counter, and the no touch zone that keeps the next ten seconds of air out of reach.
/// </summary>
public partial class LiveEditWindow
{
    // ------------------------------------------------------------------ the no touch zone

    /// <summary>
    /// How far ahead of the transmitter nothing can be changed. Ten seconds: long enough that a
    /// cue's media is already open when the zone reaches it (see
    /// <see cref="LiveAirTransmitter.PrepareSeconds"/>), short enough not to get in the way.
    /// </summary>
    private const int NoTouchSeconds = 10;

    private long NoTouchFrames => NoTouchSeconds * (long)_rate;

    /// <summary>
    /// The first ruler frame that can still be edited, or null when nothing is on air and the
    /// whole timeline is open. Everything before it has gone out or is about to.
    /// </summary>
    private long? EditableFrom => OnAirFrame() is { } onAir ? onAir + NoTouchFrames : null;

    private bool Editable(long rulerFrame) => EditableFrom is not { } from || rulerFrame >= from;

    /// <summary>Inside the zone itself: from the transmitter to ten seconds ahead of it.</summary>
    private bool InNoTouchZone(long rulerFrame) =>
        OnAirFrame() is { } onAir && rulerFrame >= onAir && rulerFrame < onAir + NoTouchFrames;

    /// <summary>Says why an edit was refused, where the selection is normally described.</summary>
    private void Refuse(string why)
    {
        LayerText.Text = why;
        LayerText.Foreground = Brush("Bad");

        ActivityLog.Shared.Warn(LogSource.LiveEdit, $"REFUSED  {why}", @event: "liveedit.refused");
    }

    private void DrawNoTouch(long onAir, double width)
    {
        double? a = TimelineZoom.XOf(Math.Max(onAir, ViewStart), ViewStart, ViewSpan, width);
        double b = Math.Min(width, (onAir + NoTouchFrames - ViewStart) / (double)ViewSpan * width);

        if (a is not { } from || b <= from)
        {
            NoTouchZone.Visibility = Visibility.Collapsed;
            return;
        }

        NoTouchZone.Margin = new Thickness(from, 0, 0, 0);
        NoTouchZone.Width = Math.Max(1, b - from);
        NoTouchZone.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ going live

    private LiveAirTransmitter? _liveTx;
    private LiveLogWindow? _logWindow;

    /// <summary>The rate the recording on air counts in, and when it rolled - what its frame numbers mean.</summary>
    private int _liveRate = 25;
    private DateTime? _liveStartedAt;

    /// <summary>What has become of each cue, by id, from the transmitter's own record.</summary>
    private readonly Dictionary<string, string> _cueStatus = new();

    private void WireLive()
    {
        LiveAir.Shared.Changed += OnLiveAirChanged;
        TidalLock.Shared.Changed += OnTidalLockChanged;
        ActivityLog.Shared.Line += OnLiveLogLine;
        EdlLogger.Shared.LockState = LockState;
        ShowLive();
    }

    private void UnwireLive()
    {
        LiveAir.Shared.Changed -= OnLiveAirChanged;
        TidalLock.Shared.Changed -= OnTidalLockChanged;
        ActivityLog.Shared.Line -= OnLiveLogLine;
        EdlLogger.Shared.Flush(TimeSpan.FromSeconds(2));

        // Owned by this window, so WPF may already be closing it as this one closes.
        try { _logWindow?.Close(); }
        catch (InvalidOperationException) { }
    }

    private void GoLive_Click(object sender, RoutedEventArgs e)
    {
        if (LiveAir.Shared.State is not (LiveAirState.Off or LiveAirState.Failed))
        {
            if (MessageBox.Show(this,
                    "End the live transmission from Live Edit?\n\nThe TX goes to black and nothing else on " +
                    "this timeline goes to air.",
                    "End live", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;

            EndLive("Live Edit went off air - ended by the operator.");
            return;
        }

        if (Ffmpeg.Locate(_settings.FfmpegPath) is null)
        {
            Refuse("Cannot go live: ffmpeg was not found, and it is what plays ingest media to air.");
            return;
        }

        var ask = new GoLiveDialog(_settings, _rate) { Owner = this };

        if (ask.ShowDialog() != true) return;

        _cueStatus.Clear();

        LiveAir.Shared.Arm(ask.Delay, ask.Headroom, ask.BoardIndex, ask.Ports, ask.Start);

        if (!PipelineState.Shared.Recording)
            StoreText.Text = "Going live as soon as the capture deck records.";
    }

    private void EndLive(string reason)
    {
        StopLiveTx();
        LiveAir.Shared.Stop(reason);
    }

    private void OnLiveAirChanged(LiveAir air) => Dispatcher.BeginInvoke(() =>
    {
        LogLiveLock(air);

        switch (air.State)
        {
            case LiveAirState.CountingDown when _liveTx is null:
                StartLiveTx(air);
                break;

            case LiveAirState.Off or LiveAirState.Failed:
                StopLiveTx();
                break;
        }

        ShowLive();
        ShowEdlState();
        Draw();
        DrawOnAir();
    });

    private void StartLiveTx(LiveAir air)
    {
        if (air.Line is not DelayLine line)
        {
            air.Fail("The capture deck opened no delay line to go live from.");
            return;
        }

        if (Ffmpeg.Locate(_settings.FfmpegPath) is not { } ffmpeg)
        {
            air.Fail("ffmpeg was not found, so ingest media could not be played to air.");
            return;
        }

        _liveRate = line.FrameRate;
        _liveStartedAt = air.RecordingStartedAt ?? PipelineState.Shared.RecordingStartedAt;

        PushCues("going live");

        LiveAirTransmitter tx;

        try
        {
            tx = new LiveAirTransmitter(air.BoardIndex, air.Ports, line, air.Delay, ffmpeg, LiveAir.Shared.CuesForAir,
                                        () => LiveAir.Shared.AudioEdits);
        }
        catch (DelayLineException ex)
        {
            air.Fail(ex.Message);
            return;
        }

        _liveTx = tx;
        tx.Status += OnLiveStatus;
        tx.Start();
    }

    /// <summary>
    /// Stops the transmitter. Off the UI thread, because stopping joins its thread, and that
    /// thread can be inside a push the card is holding for a frame.
    /// </summary>
    private void StopLiveTx()
    {
        if (_liveTx is not { } tx) return;

        _liveTx = null;
        tx.Status -= OnLiveStatus;

        _ = Task.Run(tx.Dispose);
    }

    private void OnLiveStatus(DelayStatus status) => Dispatcher.BeginInvoke(() =>
    {
        switch (status.Phase)
        {
            case DelayPhase.OnAir:
                LiveAir.Shared.WentToAir();
                break;

            case DelayPhase.Finished:
                StopLiveTx();
                LiveAir.Shared.Stop("Live Edit's transmission has played out.");
                break;

            case DelayPhase.Failed:
                StopLiveTx();
                LiveAir.Shared.Fail(status.Message);
                break;
        }

        ShowLive();
    });

    /// <summary>The Go Live button, as the state says.</summary>
    private void ShowLive()
    {
        (string text, string brush, bool filled) = LiveAir.Shared.State switch
        {
            LiveAirState.Armed => ("■ Waiting to go live", "Warn", false),
            LiveAirState.CountingDown => ("■ End Live (filling)", "Bad", false),
            LiveAirState.OnAir => ("■ END LIVE", "Bad", true),
            LiveAirState.Draining => ("■ End Live (draining)", "Warn", false),
            _ => ("● Go Live", "Bad", false),
        };

        GoLiveButton.Content = text;
        GoLiveButton.Foreground = filled ? Brushes.White : Brush(brush);
        GoLiveButton.Background = filled ? Brush("Bad") : Brushes.Transparent;
        GoLiveButton.ToolTip = LiveAir.Shared.Detail.Length > 0 ? LiveAir.Shared.Detail : GoLiveButton.ToolTip;

        // Shown, not logged again: the failure itself is already in the record, once.
        if (LiveAir.Shared.State == LiveAirState.Failed)
        {
            LayerText.Text = $"Live Edit could not go live: {LiveAir.Shared.Detail}";
            LayerText.Foreground = Brush("Bad");
        }

        ShowDelay();
    }

    /// <summary>The live state as a line for the readout, while there is no on-air mark yet.</summary>
    private string? LiveText() => LiveAir.Shared.State switch
    {
        LiveAirState.Armed => "LIVE - waiting for the capture deck to record",
        LiveAirState.CountingDown => CountdownFrames() is { } left
            ? $"LIVE - filling the delay, on air in {FormatWait(TimeSpan.FromSeconds(Math.Ceiling(left.Frames / (double)left.Rate)))}"
            : $"LIVE - filling the delay, on air in about {TidalLock.Describe(LiveAir.Shared.Delay)}",
        _ => null,
    };

    // ------------------------------------------------------------------ the timeline's EDL

    /// <summary>
    /// The timeline has changed: the ledger is told, so every edit is logged as made, changed or
    /// removed whether or not anything is live - and while live, playback is sent the whole EDL as
    /// a new version, as a conform when an edit has reached the no edit zone. Called on every edit,
    /// undo and redo. <paramref name="why"/> goes into the log.
    /// </summary>
    private void PushCues(string why = "timeline changed")
    {
        _edl.Sync(EdlEdits(), why);
        SendEdl(EdlSubmissionKind.Update, why);
    }

    // ------------------------------------------------------------------ the EDL and its log

    /// <summary>Every edit and every send, from the marks to playback taking it. See <see cref="EdlLedger"/>.</summary>
    private readonly EdlLedger _edl = new(EdlLogger.Shared);

    /// <summary>
    /// Sends the EDL as it stands to playback, if Live Edit is live: the cues in the recording's
    /// own frames, which is the number every frame in the delay carries.
    /// </summary>
    private void SendEdl(EdlSubmissionKind kind, string why)
    {
        // Only once there is a transmitter to pick it up: armed and waiting for the recorder
        // there is none, and a revision sent then would be reported unacknowledged. Going live
        // sends the EDL as the transmitter starts.
        if (!LiveAir.Shared.Active) return;

        _liveStartedAt ??= LiveAir.Shared.RecordingStartedAt ?? PipelineState.Shared.RecordingStartedAt;

        if (_liveStartedAt is null) return;

        var cues = new List<LiveCue>();

        foreach (Overlay overlay in _overlays)
        {
            if (overlay.Plan is not { } plan) continue;

            cues.Add(new LiveCue(
                overlay.Id,
                plan.Mode,
                plan.HasNoMedia ? null : overlay.Path,
                DayToRecording(plan.StartFrame),
                ToRecording(plan.Frames),
                ToRecording(plan.Covers),
                plan.Speed,
                plan.Tracks,
                overlay.GainDb));
        }

        List<AudioEdit> levels = _audioEdits
            .Select(e => new AudioEdit(DayToRecording(e.StartFrame), ToRecording(e.Frames), e.GainDb))
            .ToList();

        EdlReceipt receipt = _edl.Send(LiveAir.Shared, kind, cues, levels, ConformZone(), DateTime.UtcNow, why);

        if (!receipt.Taken && receipt.Verdict is not (EdlVerdict.NotLive or EdlVerdict.Stale))
            Refuse($"PLAYBACK REFUSED THE EDL - version {receipt.Version}: {receipt.Reason} " +
                   $"Air stays on version {_edl.AcknowledgedVersion}. Undo the change, or edit further ahead.");

        ShowEdlState();
    }

    /// <summary>
    /// Where air is, and the conform line: everything from air to the line is the no edit zone,
    /// and an edit whose first frame has reached the line is conformed. Null when nothing is on air.
    /// </summary>
    private (long Air, long Line)? ConformZone() =>
        LiveAir.Shared.State is LiveAirState.OnAir or LiveAirState.Draining && OnAirFrame() is { } air
            ? (air, air + NoTouchFrames)
            : null;

    /// <summary>
    /// Every tick while live: an edit reaching the no edit zone is logged and conformed, and the
    /// revision in flight is checked for playback's acknowledgement - and sent again, as the latest
    /// state at a new version, if playback has not picked it up in time.
    /// </summary>
    private void FollowEdl()
    {
        if (!LiveAir.Shared.Active) return;

        if (ConformZone() is { } zone)
        {
            _edl.NoteZone(zone.Air, zone.Line);

            if (_edl.DueForConform(zone.Air, zone.Line).Count > 0)
                SendEdl(EdlSubmissionKind.Conform, "an edit reached the no edit zone");
        }

        long acknowledged = _edl.AcknowledgedVersion;
        bool failing = _edl.Failing;

        if (_edl.Poll(LiveAir.Shared, DateTime.UtcNow))
            SendEdl(EdlSubmissionKind.Retry, "playback had not acknowledged the last version");

        if (_edl.AcknowledgedVersion == acknowledged && _edl.Failing == failing) return;

        // A conform confirmed changes how its deleted scene is drawn.
        ShowEdlState();
        Draw();
    }

    /// <summary>The operator's way to send the EDL again by hand, once automatic retries have stopped.</summary>
    private void ResendEdl()
    {
        if (!LiveAir.Shared.Active)
        {
            Refuse("Nothing is live from Live Edit, so there is no playback to send the EDL to.");
            return;
        }

        SendEdl(EdlSubmissionKind.Retry, "resent by the operator");
    }

    /// <summary>Where the EDL stands with playback, beside the delay.</summary>
    private void ShowEdlState()
    {
        if (!LiveAir.Shared.Active || _edl.Latest is null)
        {
            EdlStateText.Visibility = Visibility.Collapsed;
            return;
        }

        (string text, string brush, string tip) =
            _edl.Failing
                ? ($"EDL v{_edl.Pending?.Version} NOT ACKNOWLEDGED", "Bad",
                   $"Playback has not picked up version {_edl.Pending?.Version}; air is on version {_edl.AcknowledgedVersion}. " +
                   "Ctrl+Shift+E sends the EDL again.")
            : _edl.Pending is { } pending
                ? ($"EDL v{pending.Version} sent", "Warn", $"Waiting for playback to pick up version {pending.Version}.")
            : ($"EDL v{_edl.AcknowledgedVersion} ✓", "Ok",
               $"Playback is on version {_edl.AcknowledgedVersion}, the latest. Ctrl+Shift+E sends it again.");

        EdlStateText.Text = text;
        EdlStateText.Foreground = Brush(brush);
        EdlStateText.ToolTip = tip;
        EdlStateText.Visibility = Visibility.Visible;
    }

    /// <summary>The timeline's edits, as the ledger follows them: what each covers, from which capture files.</summary>
    private List<EdlEditInfo> EdlEdits()
    {
        List<IngestItem> items = IngestItems();
        var edits = new List<EdlEditInfo>();

        foreach (Overlay overlay in _overlays)
        {
            if (overlay.Plan is not { } plan) continue;

            (long from, long to) = RulerSpan(overlay, items);

            EdlSelection selection = plan.Mode switch
            {
                IngestMode.Blank => SelectionOf(from, to, items),

                // A cut takes no time on the ruler; what it covers is the recording it removes.
                IngestMode.Cut => SelectionOf(from, from + plan.Covers, items),

                _ => new EdlSelection(from, to, _rate,
                                      new[] { new EdlSourceRange(overlay.Path, 0, Math.Max(0, plan.Frames - 1), from, to - 1) },
                                      plan.HasAudio ? $"the item's own sound at {overlay.GainDb:+0.0;-0.0;0.0} dB" : "no sound of its own"),
            };

            edits.Add(new EdlEditInfo(overlay.Id, plan.Mode, selection, overlay.Path, $"{plan}|{overlay.GainDb}"));
        }

        return edits;
    }

    /// <summary>
    /// A range of the ruler, <c>[from, to)</c>, with the capture files it falls in - every file it
    /// touches when it crosses a segment boundary, and exactly which of their frames.
    /// </summary>
    private EdlSelection SelectionOf(long from, long to, List<IngestItem>? items = null)
    {
        items ??= IngestItems();

        long first = IngestTimeline.At(from, items).CapturedFrame;
        long last = to > from ? IngestTimeline.At(to - 1, items).CapturedFrame : first - 1;

        IReadOnlyList<EdlSourceRange> sources = EdlSources.Resolve(first, last,
            _placed.Select(p => new EdlSegment(p.Clip.Path, p.StartFrame, p.Frames)));

        // The sound that goes with it: every track the files carry, which a deleted scene silences.
        var tracks = _placed
            .Where(p => sources.Any(s => s.File == p.Clip.Path))
            .SelectMany(p => p.Clip.Info?.Audio ?? Array.Empty<MediaAudioStream>())
            .Select(a => $"{a.Label} ({a.Channels} ch {a.Codec})")
            .Distinct()
            .ToList();

        string audio = tracks.Count > 0
            ? $"{tracks.Count} recording track(s): {string.Join(", ", tracks)}"
            : "the recording's sound (tracks not read)";

        return new EdlSelection(from, to, _rate, sources, audio);
    }

    /// <summary>Logs a mark, with the frames and files of the range when both marks are down.</summary>
    private void LogMark(bool inPoint, bool cleared)
    {
        EdlSelection? range = _markIn is { } a && _markOut is { } b && b > a ? SelectionOf(a, b) : null;
        long at = inPoint ? _markIn ?? _at : _markOut ?? _at;

        string? file = _placed.FirstOrDefault(p => p.Holds(IngestTimeline.At(at, IngestItems()).CapturedFrame))?.Clip.Path;

        EdlLogEntry entry = new EdlLogEntry
        {
            Action = inPoint ? EdlAction.MarkIn : EdlAction.MarkOut,
            Rate = _rate,
            MarkIn = _markIn is { } i ? Stamp(i) : null,
            MarkOut = _markOut is { } o ? Stamp(o) : null,
            StartFrame = _markIn,
            SourceFiles = file is null ? Array.Empty<string>() : new[] { file },
            Message = cleared
                ? $"mark {(inPoint ? "in" : "out")} cleared at {Stamp(_at)}."
                : $"mark {(inPoint ? "in" : "out")} at {Stamp(at)} (frame {at:N0})" +
                  (range is { } r ? $"; the range is {r.DurationFrames:N0} frames ({r.DurationSeconds:0.###} s)" +
                                    (r.SpansFiles ? $" across {r.SourceFiles.Count} capture files" : "") : "") + ".",
        }.With(range);

        EdlLogger.Shared.Write(entry);
    }

    /// <summary>The locks, as every EDL log line records them.</summary>
    private static string LockState()
    {
        LiveAir air = LiveAir.Shared;

        string live = air.State == LiveAirState.Off
            ? "live edit: off"
            : $"live edit: {air.State.ToString().ToLowerInvariant()}, delay {TidalLock.Describe(air.Delay)}, " +
              $"holding {air.DelayFrames:N0} frames, EDL v{air.AppliedVersion}";

        return $"{live}; tidal lock: {TidalLock.Shared.State.ToString().ToLowerInvariant()}";
    }

    private void OnTidalLockChanged(TidalLock _) => Dispatcher.BeginInvoke(() =>
        EdlLogger.Shared.Write(new EdlLogEntry
        {
            Action = EdlAction.LockChanged,
            Message = $"tidal lock is {TidalLock.Shared.State.ToString().ToLowerInvariant()}.",
        }));

    private void LogLiveLock(LiveAir air) =>
        EdlLogger.Shared.Write(new EdlLogEntry
        {
            Action = EdlAction.LockChanged,
            UpdatedVersion = air.AppliedVersion > 0 ? air.AppliedVersion : null,
            Message = $"Live Edit's transmission is {air.State.ToString().ToLowerInvariant()}" +
                      (air.Detail.Length > 0 ? $": {air.Detail}" : "."),
        });

    /// <summary>Frames at this ruler's rate, as frames of the recording on air.</summary>
    private long ToRecording(long frames) =>
        _liveRate == _rate ? frames : (long)Math.Round(frames * _liveRate / (double)_rate);

    private long FromRecording(long frames) =>
        _liveRate == _rate ? frames : (long)Math.Round(frames * _rate / (double)_liveRate);

    /// <summary>
    /// A frame of this ruler's day as a frame of the recording on air - counted from the moment
    /// it rolled, which is the number every frame in the delay carries. Holds across midnight:
    /// the start is then before this day's midnight, and the arithmetic still runs from it.
    /// </summary>
    private long DayToRecording(long dayFrame) => ToRecording(dayFrame - FrameOn(_liveStartedAt!.Value));

    private long RecordingToDay(long recordingFrame) => FrameOn(_liveStartedAt!.Value) + FromRecording(recordingFrame);

    /// <summary>Where Live Edit's transmitter is, on this ruler.</summary>
    private long? LiveOnAirFrame()
    {
        if (LiveAir.Shared.Position is not { } position || _liveStartedAt is null) return null;

        List<IngestItem> items = IngestItems();

        long frame;

        // A cue's media: its place on the ruler plus how far into it the transmitter is.
        if (position.CueId is { } id && _overlays.FirstOrDefault(o => o.Id == id) is { Plan: { } plan } cue)
        {
            long at = IngestTimeline.RulerStart(new IngestItem(cue.Path, plan), items);
            frame = at + (long)Math.Round(FromRecording(position.CueOffset) / Math.Max(0.0001, plan.Speed));
        }
        else if (position.RecordingFrame >= 0)
        {
            frame = IngestTimeline.RulerFrameOf(RecordingToDay(position.RecordingFrame), items);
        }
        else
        {
            return null;
        }

        return frame >= 0 && frame < _dayFrames ? frame : null;
    }

    // ------------------------------------------------------------------ the delay counter

    /// <summary>
    /// The delay from air, in frames of this ruler: what is being held now, plus every insert
    /// still to come and less every scene cut still to come. Null when nothing is on air.
    ///
    /// For Live Edit's own transmission it is dynamic - an insert dropped ahead of the zone adds
    /// its length straight away, and deleting it takes it away again. For the playback deck's
    /// tidal lock it is that deck's fixed delay: that transmission does not play the timeline.
    /// </summary>
    private long? DelayCounterFrames()
    {
        // Memoised for a frame, as the on-air position is: the view's width is capped by this,
        // and every draw and hit test asks for the width.
        DateTime now = DateTime.UtcNow;

        if ((now - _delayCachedAt).TotalMilliseconds < 30) return _delayCached;

        _delayCachedAt = now;
        _delayCached = ReadDelayCounter();

        return _delayCached;
    }

    private long? _delayCached;
    private DateTime _delayCachedAt = DateTime.MinValue;

    private long? ReadDelayCounter()
    {
        LiveAirState state = LiveAir.Shared.State;

        if (HeldDelayFrames() is { } held &&
            LiveAir.Shared.State is LiveAirState.Armed or LiveAirState.CountingDown or LiveAirState.OnAir or LiveAirState.Draining)
            return Math.Max(0, held + PendingDelayChange());

        return HeldDelayFrames();
    }

    /// <summary>
    /// The delay as it stands this moment - what air is actually behind the recorder - before
    /// any insert or deleted scene still ahead of air has had its effect. Null when nothing is on air.
    /// </summary>
    private long? HeldDelayFrames()
    {
        LiveAirState state = LiveAir.Shared.State;

        if (state is LiveAirState.Armed or LiveAirState.CountingDown or LiveAirState.OnAir or LiveAirState.Draining)
            return _liveTx is { } tx && state != LiveAirState.Armed
                ? FromRecording(tx.TargetFrames)
                : (long)Math.Round(LiveAir.Shared.Delay.TotalSeconds * _rate);

        if (TidalLock.Shared.State is TidalLockState.OnAir or TidalLockState.Draining)
            return (long)Math.Round(TidalLock.Shared.Delay.TotalSeconds * _rate);

        return null;
    }

    /// <summary>
    /// The next insert or deleted scene air will reach: how many frames of this ruler until it
    /// does, and what it does to the delay. Null when there is none ahead, or no on-air mark.
    /// </summary>
    private (long In, long Change)? NextDelayChange()
    {
        if (LiveAir.Shared.State is LiveAirState.Off or LiveAirState.Failed || OnAirFrame() is not { } air)
            return null;

        List<IngestItem> items = IngestItems();

        return _overlays
            .Where(o => o.Plan is { Mode: IngestMode.Insert or IngestMode.Cut })
            .Where(o => !_cueStatus.TryGetValue(o.Id, out string? s) || s == "prepared")
            .Select(o => (Plan: o.Plan!.Value, At: IngestTimeline.RulerStart(new IngestItem(o.Path, o.Plan!.Value), items)))
            .Where(x => x.At >= air)
            .OrderBy(x => x.At)
            .Select(x => ((long In, long Change)?)(x.At - air, x.Plan.Mode == IngestMode.Insert ? x.Plan.Frames : -x.Plan.Covers))
            .FirstOrDefault();
    }

    /// <summary>
    /// What the inserts and cuts not yet played will do to the delay. One the transmitter has
    /// reached is already in what it holds; one behind the transmitter that never played will
    /// not now, and does not count.
    /// </summary>
    private long PendingDelayChange()
    {
        long? onAir = OnAirFrame();
        List<IngestItem> items = IngestItems();

        long change = 0;

        foreach (Overlay overlay in _overlays)
        {
            if (overlay.Plan is not { } plan || plan.Mode is not (IngestMode.Insert or IngestMode.Cut)) continue;
            if (_cueStatus.ContainsKey(overlay.Id) && _cueStatus[overlay.Id] != "prepared") continue;

            long at = IngestTimeline.RulerStart(new IngestItem(overlay.Path, plan), items);
            if (onAir is { } air && at < air) continue;

            change += plan.Mode == IngestMode.Insert ? plan.Frames : -plan.Covers;
        }

        return change;
    }

    /// <summary>The most the delay may become: the delay gone live with, plus the room made for inserts.</summary>
    private long? DelayCapacityFrames() =>
        LiveAir.Shared.Active || LiveAir.Shared.State == LiveAirState.Armed
            ? (long)Math.Round((LiveAir.Shared.Delay + LiveAir.Shared.Headroom).TotalSeconds * _rate)
            : null;

    private void ShowDelay()
    {
        if (DelayCounterFrames() is not { } delay)
        {
            DelayText.Visibility = Visibility.Collapsed;
            return;
        }

        bool ours = LiveAir.Shared.State is not (LiveAirState.Off or LiveAirState.Failed);

        // The delay changes when air reaches a deleted scene or an insert, not when it is made: up
        // to then air is still as far behind as it was. So the readout is the delay now, and -
        // while one is still ahead - what it becomes and how soon.
        long now = HeldDelayFrames() ?? delay;

        if (ours && delay != now && NextDelayChange() is { } next)
        {
            long after = Math.Max(0, now + next.Change);

            DelayText.Text = $"DELAY {new Timecode(now, _rate)} → {new Timecode(after, _rate)} in " +
                             FormatWait(TimeSpan.FromSeconds(Math.Ceiling(next.In / (double)_rate)));

            if (after != delay) DelayText.Text += $"  (then {new Timecode(delay, _rate)})";
        }
        else
        {
            DelayText.Text = $"DELAY {new Timecode(delay, _rate)}";
        }

        DelayText.ToolTip = ours
            ? $"How far behind the recording air is: {TidalLock.Describe(LiveAir.Shared.Delay)} chosen, " +
              "plus every insert, less every deleted scene - each taking effect as air reaches it. " +
              (DelayCapacityFrames() is { } most ? $"It can grow to {new Timecode(most, _rate)}." : "")
            : "The playback deck's tidal lock delay. That transmission does not play this timeline's inserts or cuts.";
        DelayText.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ cue status

    private void OnLiveLogLine(LogLine line)
    {
        if (line.Correlation is not { } id ||
            line.Event?.StartsWith("liveair.cue.", StringComparison.Ordinal) != true)
            return;

        string what = line.Event["liveair.cue.".Length..];

        Dispatcher.BeginInvoke(() =>
        {
            _cueStatus[id] = what;
            ShowDelay();
        });
    }

    // ------------------------------------------------------------------ deleting a scene

    private void DeleteScene_Click(object sender, RoutedEventArgs e) => DeleteScene();

    /// <summary>
    /// Deletes the marked range from air - as black picture and silence for exactly its length,
    /// not by taking it out. Nothing is removed: the captured files are untouched, the timeline
    /// keeps its length, nothing after the range moves, and the delay stays as it is. What changes
    /// is the EDL, which from here tells playback not to play those frames.
    ///
    /// <b>Changing a deleted scene.</b> With one selected - or with marks that overlap one - Delete
    /// changes that scene to the marked range instead of making another. That is how a deletion is
    /// lengthened or shortened, including after its first frame has been conformed: whatever has
    /// not reached the conform line can still move. Shortening one plays the original frames it
    /// gives back.
    /// </summary>
    private void DeleteScene()
    {
        if (_markIn is not { } from || _markOut is not { } to || to <= from) return;

        List<IngestItem> items = IngestItems();

        // The deleted scene this changes, if it is a change: the one selected, if the marks reach
        // it, or else the only one the marks overlap.
        List<Overlay> overlapped = _overlays
            .Where(o => o.IsBlank && RulerSpan(o, items) is var span && span.From <= to && span.To >= from)
            .ToList();

        Overlay? target = _selected is { IsBlank: true } chosen && overlapped.Contains(chosen)
            ? chosen
            : overlapped.Count == 1 ? overlapped[0] : null;

        if (target is null && overlapped.Count > 1)
        {
            Refuse("The marked range runs over more than one deleted scene. Select the one to change, or mark a range that reaches only one.");
            return;
        }

        if (overlapped.Any(o => o != target && RulerSpan(o, items) is var span && span.From < to && span.To > from))
        {
            Refuse("The marked range runs into another deleted scene. Change that one, or mark around it.");
            return;
        }

        // Over an ingest item the picture is already the item's, and under an insert or a cut the
        // ruler is not the recording - so a deleted scene sits on the recording alone.
        if (_overlays.Any(o => o.Plan is { Mode: not IngestMode.Blank } plan &&
                               RulerSpan(o, items) is var span &&
                               (plan.Mode == IngestMode.Cut ? span.From > from && span.From < to : span.From < to && span.To > from)))
        {
            Refuse("The marked range runs into an ingest item or a scene cut. Delete the scene either side of it.");
            return;
        }

        long capturedFrom = IngestTimeline.At(from, items).CapturedFrame;

        if (!IngestPlacement.TryPlanBlank(capturedFrom, capturedFrom + (to - from), out IngestPlan plan, out string? problem))
        {
            Refuse(problem!);
            return;
        }

        if (target is { Plan: { } before })
        {
            if (Equals(before, plan)) return;

            // Only what changes has to be ahead of the line. A scene whose start has been
            // conformed can still be lengthened or shortened while its end has not reached it.
            if (EdlDiff.EarliestChange(before, plan) is { } changed &&
                IngestTimeline.RulerFrameOf(changed, items) is var changedAt && !Editable(changedAt))
            {
                Refuse($"NO EDIT ZONE - that change would alter air at {Stamp(changedAt)}, which has gone out or is about to. " +
                       "A deleted scene can be changed only where air has not reached the conform line.");
                return;
            }

            Remember($"changing the deleted scene at {Stamp(RulerSpan(target, items).From)}");

            target.StartFrame = plan.StartFrame;
            target.Plan = plan;
        }
        else
        {
            if (!Editable(from))
            {
                Refuse("NO EDIT ZONE - a scene can only be deleted ahead of the next ten seconds of air.");
                return;
            }

            target = new Overlay
            {
                Path = "",
                StartFrame = plan.StartFrame,
                Plan = plan,
            };

            Remember($"deleting the scene at {Stamp(from)}");

            _overlays.Add(target);
        }

        _selected = target;
        _selectedSound = false;
        _audioPiece = null;
        _markIn = _markOut = null;

        PushCues(overlapped.Contains(target) ? "deleted scene changed" : "scene deleted");
        FollowAudioLevel(force: true);
        Draw();
        DrawOnAir();
        ShowPosition();
        ShowLayer();
    }

    /// <summary>Where a placement sits on the ruler, start included and end not.</summary>
    private static (long From, long To) RulerSpan(Overlay overlay, List<IngestItem> items)
    {
        long at = IngestTimeline.RulerStart(new IngestItem(overlay.Path, overlay.Plan!.Value), items);
        return (at, at + overlay.Frames);
    }

    /// <summary>
    /// Whether an insert this long still fits in the room made for inserts. Only asked while
    /// live from here; otherwise there is no ring to outgrow.
    /// </summary>
    private bool InsertFits(long frames, out string? why)
    {
        why = null;

        if (DelayCapacityFrames() is not { } most || DelayCounterFrames() is not { } delay) return true;

        // A second of guard, as the ring itself keeps two.
        if (delay + frames <= most - _rate) return true;

        why = $"That insert would make the delay {new Timecode(delay + frames, _rate)}, and Live Edit went live " +
              $"with room for {new Timecode(most, _rate)}. Go live again with more insert room, or delete a scene first.";
        return false;
    }

    // ------------------------------------------------------------------ the logs

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        if (_logWindow is { IsLoaded: true })
        {
            _logWindow.Activate();
            return;
        }

        _logWindow = new LiveLogWindow(EdlRows, LiveSummary) { Owner = this };
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show();
    }

    private string LiveSummary() =>
        LiveAir.Shared.State switch
        {
            LiveAirState.OnAir => $"LIVE  delay {(DelayCounterFrames() is { } d ? new Timecode(d, _rate).ToString() : "-")}",
            LiveAirState.Off => "not live",
            var other => other.ToString().ToUpperInvariant(),
        };

    /// <summary>The timeline's EDL, in the order air reaches it.</summary>
    private IReadOnlyList<EdlRow> EdlRows()
    {
        List<IngestItem> items = IngestItems();
        long? onAir = OnAirFrame();

        return _overlays
            .Where(o => o.Plan is not null)
            .Select(o => (Overlay: o, At: IngestTimeline.RulerStart(new IngestItem(o.Path, o.Plan!.Value), items)))
            .OrderBy(x => x.At)
            .Select(x =>
            {
                IngestPlan plan = x.Overlay.Plan!.Value;

                string status = _cueStatus.TryGetValue(x.Overlay.Id, out string? s)
                    ? s switch
                    {
                        "prepared" => "CUED",
                        "playing" => "ON AIR",
                        "done" or "cut" => "AIRED",
                        "failed" or "openfailed" => "FAILED",
                        _ => s.ToUpperInvariant(),
                    }
                    : onAir is { } air && x.At < air ? (LiveAir.Shared.Active ? "MISSED" : "PAST")
                    : LiveAir.Shared.Active ? "WAITING"
                    : "NOT LIVE";

                string brush = status switch
                {
                    "ON AIR" => "Bad",
                    "CUED" or "WAITING" => "Ok",
                    "AIRED" or "PAST" => "Muted",
                    "FAILED" or "MISSED" => "Warn",
                    _ => "Muted",
                };

                string @in = onAir is { } a && x.At >= a ? new Timecode(x.At - a, _rate).ToString() : "-";

                string what = plan.Mode switch
                {
                    IngestMode.Cut => "scene cut",
                    IngestMode.Blank => _edl.Edit(x.Overlay.Id) is { ConformedVersion: { } v }
                        ? $"scene deleted - black and silence (conformed, EDL v{v})"
                        : "scene deleted - black and silence",
                    _ => $"{x.Overlay.Name}  ({plan.Mode.ToString().ToLowerInvariant()})",
                };

                long length = plan.Mode == IngestMode.Cut ? plan.Covers : plan.Frames;

                return new EdlRow(Stamp(x.At), @in, what,
                                  (plan.Mode == IngestMode.Cut ? "-" : "") + new Timecode(length, _rate),
                                  status, Brush(brush));
            })
            .ToList();
    }

    // ------------------------------------------------------------------ the countdown to air

    /// <summary>
    /// Frames until the recording goes to air, while the delay is filling: the transmitter sends
    /// black until the line holds its whole delay, so it is that delay less what is already in.
    /// Null when nothing is counting down.
    /// </summary>
    private (long Frames, int Rate)? CountdownFrames()
    {
        if (LiveAir.Shared.State != LiveAirState.CountingDown ||
            LiveAir.Shared.Line is not { FrameRate: > 0 } line)
            return null;

        long target = _liveTx?.TargetFrames ??
                      (long)Math.Ceiling(LiveAir.Shared.Delay.TotalSeconds * line.FrameRate);

        return (Math.Max(0, target - line.FramesWritten), line.FrameRate);
    }

    private LiveAirState _countdownSeen;
    private DateTime? _wentOnAirAt;

    /// <summary>
    /// Every tick: the countdown on the big screen and the Go Live button, then "ON AIR" for a
    /// moment when it gets there.
    /// </summary>
    private void ShowCountdown()
    {
        LiveAirState state = LiveAir.Shared.State;

        if (state == LiveAirState.OnAir && _countdownSeen == LiveAirState.CountingDown)
            _wentOnAirAt = DateTime.UtcNow;

        _countdownSeen = state;

        if (CountdownFrames() is { } left)
        {
            TimeSpan wait = TimeSpan.FromSeconds(left.Frames / (double)left.Rate);
            bool close = wait <= TimeSpan.FromSeconds(10);

            CountdownLabel.Text = "ON AIR IN";
            CountdownText.Text = new Timecode(left.Frames, left.Rate).ToString();
            CountdownText.Foreground = close ? Brush("Bad") : Brushes.White;
            CountdownDetail.Text = $"at {DateTime.Now + wait:HH:mm:ss}  -  delay {TidalLock.Describe(LiveAir.Shared.Delay)}";
            CountdownBadge.BorderBrush = Brush(close ? "Bad" : "Warn");
            CountdownBadge.Visibility = Visibility.Visible;

            GoLiveButton.Content = $"■ End Live (on air in {FormatWait(wait)})";
            return;
        }

        if (state == LiveAirState.Armed)
        {
            CountdownLabel.Text = "GOING LIVE";
            CountdownText.Text = "waiting";
            CountdownText.Foreground = Brushes.White;
            CountdownDetail.Text = "for the capture deck to record";
            CountdownBadge.BorderBrush = Brush("Warn");
            CountdownBadge.Visibility = Visibility.Visible;
            return;
        }

        if (state == LiveAirState.OnAir && _wentOnAirAt is { } at && DateTime.UtcNow - at < TimeSpan.FromSeconds(3))
        {
            CountdownLabel.Text = "";
            CountdownText.Text = "ON AIR";
            CountdownText.Foreground = Brush("Bad");
            CountdownDetail.Text = $"delay {TidalLock.Describe(LiveAir.Shared.Delay)}";
            CountdownBadge.BorderBrush = Brush("Bad");
            CountdownBadge.Visibility = Visibility.Visible;
            return;
        }

        CountdownBadge.Visibility = Visibility.Collapsed;
    }

    /// <summary>A wait as an operator says it: 1:23, or 1:02:03 past the hour.</summary>
    private static string FormatWait(TimeSpan wait) =>
        wait.TotalHours >= 1 ? $"{(int)wait.TotalHours}:{wait.Minutes:00}:{wait.Seconds:00}" : $"{(int)wait.TotalMinutes}:{wait.Seconds:00}";
}
