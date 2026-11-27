using System.IO;

namespace Emerald.Core;

/// <summary>Where an edit stands with playback.</summary>
public enum EdlConformStatus
{
    /// <summary>Its first frame has not reached the no edit zone. It is in the EDL, and can still change freely.</summary>
    NotConformed,

    /// <summary>Sent as a conform; playback has taken it but not yet picked it up.</summary>
    Conforming,

    /// <summary>Playback has picked up a revision carrying this edit as it now stands.</summary>
    Conformed,

    /// <summary>Playback refused the conform. What is on air is the revision before it.</summary>
    Rejected,
}

/// <summary>One edit as the ledger follows it: what it covers, how many times it has changed, and where it stands with playback.</summary>
public sealed class EdlEditRecord
{
    public required string EditId { get; init; }
    public required IngestMode Mode { get; set; }
    public required EdlSelection Selection { get; set; }

    /// <summary>The thing it lays in, for an ingest item; empty for a deleted scene.</summary>
    public string Media { get; set; } = "";

    /// <summary>Anything about it that changes what goes to air. A change here, with the range unchanged, is still a modification.</summary>
    public string Signature { get; set; } = "";

    /// <summary>One for the edit as made, and one more for every change after.</summary>
    public int Revision { get; set; } = 1;

    public EdlConformStatus Conform { get; set; }

    /// <summary>The revision of this edit a conform was last sent for, so one is sent once per change and no more.</summary>
    public int? ConformRequestedRevision { get; set; }

    /// <summary>The EDL version that conform went out in, and the one playback acknowledged.</summary>
    public long? ConformVersion { get; set; }
    public long? ConformedVersion { get; set; }
    public string? ConformTransaction { get; set; }

    public bool ReachedZone { get; set; }
}

/// <summary>What the window knows about an edit, handed to <see cref="EdlLedger.Sync"/>.</summary>
public sealed record EdlEditInfo(string EditId, IngestMode Mode, EdlSelection Selection, string Media, string Signature);

/// <summary>
/// The life of every edit from the marks to playback, and the versioned handshake that gets the
/// EDL there.
///
/// <b>Edits.</b> <see cref="Sync"/> is given the timeline as it stands after every change - by an
/// edit, an undo, a redo, a day moving on - and works out for itself what was created, changed and
/// deleted. One place that compares, rather than a log call at each of the dozen places that edit,
/// so a path that changes the timeline cannot forget to say so.
///
/// <b>Revisions.</b> Every send is the whole EDL at a new version, above anything playback holds.
/// Playback refuses a version that is not newer, so an older revision can never overwrite a newer
/// one, and recognises a repeated transaction, so nothing is applied twice.
///
/// <b>Conform.</b> When an edit's first frame reaches the no edit zone the whole EDL is sent as a
/// conform for it - before the frames themselves get to air, not after. If the edit changes again
/// while it is still in reach (an out point moved before it got to the line), it is conformed
/// again at a new version that supersedes the first.
///
/// <b>Acknowledgement.</b> Taken is not applied. A revision counts as on air only once playback
/// says it has picked it up; until then it is in flight, and one that is not picked up in time is
/// logged as a failure and sent again - as the latest state, at a new version, never by replaying
/// an old one.
///
/// Pure: no clock, no window, no card. The caller passes the time and the playback.
/// </summary>
public sealed class EdlLedger
{
    /// <summary>How long playback has to pick a revision up before it counts as not acknowledged.</summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Sends of the same state before the ledger stops retrying and waits for the next edit or a manual resend.</summary>
    public const int MaxAttempts = 3;

    private readonly EdlLogger _log;
    private readonly Dictionary<string, EdlEditRecord> _edits = new();

    private long _version;
    private EdlRevision? _latest;
    private long _acknowledged;

    private sealed record InFlight(EdlRevision Revision, DateTime SentAt, int Attempt, bool Reported);

    private InFlight? _inFlight;

    public EdlLedger(EdlLogger log) => _log = log;

    public IReadOnlyCollection<EdlEditRecord> Edits => _edits.Values;

    public EdlEditRecord? Edit(string editId) => _edits.GetValueOrDefault(editId);

    /// <summary>The newest version issued.</summary>
    public long Version => _version;

    /// <summary>The newest version playback has acknowledged picking up.</summary>
    public long AcknowledgedVersion => _acknowledged;

    public EdlRevision? Latest => _latest;

    /// <summary>A revision sent and not yet acknowledged.</summary>
    public EdlRevision? Pending => _inFlight?.Revision;

    /// <summary>True once retries have run out: playback is not picking the EDL up.</summary>
    public bool Failing { get; private set; }

    // ------------------------------------------------------------------ edits

    /// <summary>
    /// The timeline as it now stands. Logs every edit that appeared, changed or went, with the
    /// state before and after. <paramref name="why"/> says what did it - "deleting a scene",
    /// "undo" - and goes into each line.
    /// </summary>
    public void Sync(IReadOnlyList<EdlEditInfo> edits, string why)
    {
        foreach (EdlEditRecord gone in _edits.Values.Where(r => edits.All(e => e.EditId != r.EditId)).ToList())
        {
            _edits.Remove(gone.EditId);

            _log.Write(new EdlLogEntry
            {
                Action = EdlAction.EditDeleted,
                EditId = gone.EditId,
                ConformStatus = gone.Conform.ToString(),
                PreviousVersion = gone.ConformedVersion,
                Instruction = gone.Mode == IngestMode.Blank
                    ? $"restore the original frames {gone.Selection.FirstFrame:N0} to {gone.Selection.LastFrame:N0} to air"
                    : $"take {Describe(gone)} off air",
                Message = $"{Describe(gone)} removed ({why}).",
            }.With(gone.Selection));
        }

        foreach (EdlEditInfo info in edits)
        {
            if (!_edits.TryGetValue(info.EditId, out EdlEditRecord? record))
            {
                record = new EdlEditRecord
                {
                    EditId = info.EditId,
                    Mode = info.Mode,
                    Selection = info.Selection,
                    Media = info.Media,
                    Signature = info.Signature,
                };

                _edits.Add(info.EditId, record);

                _log.Write(new EdlLogEntry
                {
                    Action = EdlAction.EditCreated,
                    EditId = info.EditId,
                    ConformStatus = record.Conform.ToString(),
                    Instruction = InstructionFor(record),
                    Message = $"{Describe(record)} created ({why}): {record.Selection.MarkInTimecode} to " +
                              $"{record.Selection.MarkOutTimecode}, {record.Selection.DurationFrames:N0} frames" +
                              (record.Selection.SpansFiles ? $" across {record.Selection.SourceFiles.Count} capture files" : "") + ".",
                }.With(record.Selection));

                continue;
            }

            bool moved = record.Selection.MarkIn != info.Selection.MarkIn || record.Selection.MarkOut != info.Selection.MarkOut;

            if (!moved && record.Signature == info.Signature && record.Mode == info.Mode)
            {
                // The same edit, but the files under it may have been re-read. Kept current
                // without a line: nothing about air has changed.
                record.Selection = info.Selection;
                continue;
            }

            EdlSelection before = record.Selection;

            record.Mode = info.Mode;
            record.Selection = info.Selection;
            record.Media = info.Media;
            record.Signature = info.Signature;
            record.Revision++;

            _log.Write(new EdlLogEntry
            {
                Action = EdlAction.EditModified,
                EditId = record.EditId,
                ConformStatus = record.Conform.ToString(),
                PreviousVersion = record.ConformedVersion,
                Instruction = InstructionFor(record) + ChangeOf(before, info.Selection, record.Mode),
                Message = $"{Describe(record)} changed ({why}) to revision {record.Revision}: " +
                          $"{before.MarkInTimecode}-{before.MarkOutTimecode} ({before.DurationFrames:N0} frames) became " +
                          $"{info.Selection.MarkInTimecode}-{info.Selection.MarkOutTimecode} ({info.Selection.DurationFrames:N0} frames)" +
                          (record.Conform == EdlConformStatus.NotConformed ? "." : " - already conformed, so it will be conformed again."),
            }.With(info.Selection));
        }
    }

    /// <summary>Logs every edit, not yet gone by <paramref name="air"/>, whose first frame has reached <paramref name="conformLine"/> for the first time.</summary>
    public void NoteZone(long air, long conformLine)
    {
        foreach (EdlEditRecord record in _edits.Values.Where(r => !r.ReachedZone && InReach(r, air, conformLine)))
        {
            record.ReachedZone = true;

            _log.Write(new EdlLogEntry
            {
                Action = EdlAction.NoEditZone,
                EditId = record.EditId,
                ConformStatus = record.Conform.ToString(),
                Message = $"{Describe(record)} reached the no edit zone at {record.Selection.MarkInTimecode}.",
            }.With(record.Selection));
        }
    }

    /// <summary>
    /// The edits that need conforming now: their first frame is at or past the line, and the
    /// revision of them playback was last told about is not the one they are at.
    /// </summary>
    public IReadOnlyList<EdlEditRecord> DueForConform(long air, long conformLine) =>
        _edits.Values
              .Where(r => InReach(r, air, conformLine) && r.ConformRequestedRevision != r.Revision)
              .OrderBy(r => r.Selection.FirstFrame)
              .ToList();

    /// <summary>
    /// Its first frame is at or past the conform line, and its last has not gone by air. One wholly
    /// behind air - an edit made before going live - is history, not something to conform.
    /// </summary>
    private static bool InReach(EdlEditRecord record, long air, long conformLine) =>
        record.Selection.FirstFrame <= conformLine && record.Selection.LastFrame >= air;

    // ------------------------------------------------------------------ sending

    /// <summary>
    /// Sends the EDL as it now stands. A conform when any edit is due one at
    /// <paramref name="conformLine"/>; otherwise an update, or a retry when that is what
    /// <paramref name="kind"/> says.
    /// </summary>
    public EdlReceipt Send(IEdlPlayback playback, EdlSubmissionKind kind,
                           IReadOnlyList<LiveCue> cues, IReadOnlyList<AudioEdit> audio,
                           (long Air, long Line)? zone, DateTime now, string why)
    {
        List<EdlEditRecord> conforming = zone is { } z ? DueForConform(z.Air, z.Line).ToList() : new();

        if (conforming.Count > 0 && kind != EdlSubmissionKind.Retry) kind = EdlSubmissionKind.Conform;

        int attempt = kind == EdlSubmissionKind.Retry && _inFlight is { } previous ? previous.Attempt + 1 : 1;

        EdlRevision revision = Issue(playback, kind, cues, audio, conforming, now, why);
        EdlReceipt receipt = playback.Submit(revision);

        // Someone else got a newer version in first - another window, or this one before it was
        // reopened. Issued again above it, once: the content is the latest either way.
        if (receipt.Verdict == EdlVerdict.Stale && playback.AcceptedVersion >= revision.Version)
        {
            Said(revision, receipt, conforming);

            revision = Issue(playback, kind, cues, audio, conforming, now, why + ", reissued above a newer version");
            receipt = playback.Submit(revision);
        }

        Said(revision, receipt, conforming);

        if (receipt.Taken)
        {
            _inFlight = new InFlight(revision, now, attempt, false);

            foreach (EdlEditRecord record in conforming)
            {
                record.Conform = EdlConformStatus.Conforming;
                record.ConformVersion = revision.Version;
                record.ConformTransaction = revision.TransactionId;
            }
        }
        else
        {
            foreach (EdlEditRecord record in conforming) record.Conform = EdlConformStatus.Rejected;
        }

        // Sent for this revision of each, accepted or not: a refused conform is not sent again
        // every tick. The next change to the edit, or a resend, asks again.
        foreach (EdlEditRecord record in conforming) record.ConformRequestedRevision = record.Revision;

        return receipt;
    }

    /// <summary>
    /// Checks on the revision in flight. Logs the acknowledgement when playback has picked it up;
    /// when it has not in time, logs that and returns true - the caller should resend the latest
    /// state as a <see cref="EdlSubmissionKind.Retry"/>. Gives up asking after
    /// <see cref="MaxAttempts"/>, but still recognises a late acknowledgement.
    /// </summary>
    public bool Poll(IEdlPlayback playback, DateTime now)
    {
        if (_inFlight is not { } flight) return false;

        long applied = playback.AppliedVersion;

        if (applied >= flight.Revision.Version)
        {
            _inFlight = null;
            _acknowledged = Math.Max(_acknowledged, applied);
            Failing = false;

            TimeSpan took = now - flight.SentAt;
            bool late = took > AckTimeout;

            List<EdlEditRecord> confirmed = _edits.Values
                .Where(r => r.Conform == EdlConformStatus.Conforming && r.ConformVersion <= applied)
                .ToList();

            foreach (EdlEditRecord record in confirmed)
            {
                record.Conform = EdlConformStatus.Conformed;
                record.ConformedVersion = applied;
            }

            _log.Write(new EdlLogEntry
            {
                Action = EdlAction.PlaybackAcknowledged,
                TransactionId = flight.Revision.TransactionId,
                UpdatedVersion = applied,
                PlaybackResponse = $"applied version {applied}" + (applied > flight.Revision.Version ? $" (newer than {flight.Revision.Version}, which it supersedes)" : ""),
                ConformStatus = confirmed.Count > 0 ? $"{EdlConformStatus.Conformed}: {string.Join(", ", confirmed.Select(r => r.EditId))}" : null,
                Message = $"playback picked up version {applied} after {took.TotalMilliseconds:N0} ms" +
                          (late ? " - late, after it had been reported missing" : "") +
                          (confirmed.Count > 0 ? $"; {confirmed.Count} edit(s) conformed." : "."),
            });

            return false;
        }

        if (now - flight.SentAt < AckTimeout || flight.Reported) return false;

        bool retry = flight.Attempt < MaxAttempts;

        _inFlight = flight with { Reported = true };
        Failing = !retry;

        _log.Write(new EdlLogEntry
        {
            Action = EdlAction.OperationFailed,
            TransactionId = flight.Revision.TransactionId,
            UpdatedVersion = flight.Revision.Version,
            PreviousVersion = applied,
            PlaybackResponse = $"not acknowledged - playback is still on version {applied}",
            Error = $"version {flight.Revision.Version} was not picked up within {AckTimeout.TotalSeconds:0} s " +
                    $"(attempt {flight.Attempt} of {MaxAttempts})",
            Message = retry
                ? $"playback has not picked up version {flight.Revision.Version}; sending the latest EDL again."
                : $"playback has not picked up version {flight.Revision.Version} after {MaxAttempts} attempts. " +
                  "No more automatic retries: the next edit, or a resend, sends it again.",
        });

        return retry;
    }

    /// <summary>Forgets the edits, as when the day changes. Versions carry on, so nothing older can come back.</summary>
    public void Clear(string why) => Sync(Array.Empty<EdlEditInfo>(), why);

    /// <summary>
    /// Lets go of edits that ended long ago, without logging them as removed: nobody took them off
    /// air, they simply finished. Live Edit lets go of an edit a day after it ended, so what it
    /// holds stops growing for as long as the window is open. Returns how many were held.
    /// </summary>
    public int Forget(IEnumerable<string> editIds) => editIds.Count(id => _edits.Remove(id));

    // ------------------------------------------------------------------ the pieces

    private EdlRevision Issue(IEdlPlayback playback, EdlSubmissionKind kind,
                              IReadOnlyList<LiveCue> cues, IReadOnlyList<AudioEdit> audio,
                              List<EdlEditRecord> conforming, DateTime now, string why)
    {
        long previous = Math.Max(_version, playback.AcceptedVersion);
        _version = previous + 1;

        var revision = new EdlRevision(_version, Guid.NewGuid().ToString("N"), kind, cues, audio, now,
                                       conforming.Select(r => r.EditId).ToList());

        bool first = _latest is null;
        long? supersedes = _latest?.Version;
        _latest = revision;

        // The superseded in-flight revision is answered by this one: its acknowledgement, if it
        // comes, is this one's or older, and Poll judges by version.
        _log.Write(new EdlLogEntry
        {
            Action = first ? EdlAction.EdlGenerated : EdlAction.EdlUpdated,
            TransactionId = revision.TransactionId,
            PreviousVersion = supersedes,
            UpdatedVersion = revision.Version,
            Instruction = Summary(cues),
            Message = first
                ? $"version {revision.Version} generated ({why}): {Summary(cues)}."
                : $"version {revision.Version} supersedes version {supersedes} ({why}): {Summary(cues)}.",
        });

        foreach (EdlEditRecord record in conforming)
        {
            _log.Write(new EdlLogEntry
            {
                Action = EdlAction.ConformStarted,
                EditId = record.EditId,
                TransactionId = revision.TransactionId,
                PreviousVersion = record.ConformedVersion,
                UpdatedVersion = revision.Version,
                ConformStatus = record.ConformedVersion is null ? "CONFORM" : "CONFORM (re-conform of a changed edit)",
                Instruction = InstructionFor(record),
                Message = $"CONFORM {Describe(record)} in version {revision.Version}" +
                          (record.ConformedVersion is { } was ? $", superseding version {was}" : "") +
                          $": {InstructionFor(record)}.",
            }.With(record.Selection));
        }

        return revision;
    }

    private void Said(EdlRevision revision, EdlReceipt receipt, List<EdlEditRecord> conforming)
    {
        string response = receipt.Verdict + (receipt.Reason is { } reason ? $": {reason}" : "");

        _log.Write(new EdlLogEntry
        {
            Action = EdlAction.EdlSent,
            TransactionId = revision.TransactionId,
            UpdatedVersion = revision.Version,
            PreviousVersion = _acknowledged > 0 ? _acknowledged : null,
            ConformStatus = revision.Kind == EdlSubmissionKind.Conform ? "CONFORM" : revision.Kind.ToString().ToUpperInvariant(),
            PlaybackResponse = response,
            Message = $"version {revision.Version} sent to playback as {revision.Kind.ToString().ToUpperInvariant()} - {response}.",
        });

        if (receipt.Taken) return;

        _log.Write(new EdlLogEntry
        {
            Action = EdlAction.PlaybackRejected,
            TransactionId = revision.TransactionId,
            UpdatedVersion = revision.Version,
            PlaybackResponse = response,
            ConformStatus = conforming.Count > 0 ? EdlConformStatus.Rejected.ToString() : null,
            Message = $"playback refused version {revision.Version}: {receipt.Reason}",
        });

        // Stale is answered by reissuing, and not being live is not a fault. Anything else is
        // something the operator has to hear about.
        if (receipt.Verdict is EdlVerdict.Stale or EdlVerdict.NotLive) return;

        foreach (EdlEditRecord? record in conforming.DefaultIfEmpty())
        {
            _log.Write(new EdlLogEntry
            {
                Action = EdlAction.OperationFailed,
                EditId = record?.EditId,
                TransactionId = revision.TransactionId,
                UpdatedVersion = revision.Version,
                PreviousVersion = _acknowledged > 0 ? _acknowledged : null,
                PlaybackResponse = response,
                Error = receipt.Reason,
                Message = record is null
                    ? $"version {revision.Version} was not applied; air stays on version {_acknowledged}."
                    : $"{Describe(record)} could not be conformed; air stays on version {_acknowledged}.",
            }.With(record?.Selection));
        }
    }

    private static string Describe(EdlEditRecord record) => record.Mode switch
    {
        IngestMode.Blank => "deleted scene",
        IngestMode.Cut => "scene cut",
        _ => $"{record.Mode.ToString().ToLowerInvariant()} of {Path.GetFileName(record.Media)}",
    };

    /// <summary>What playback is told to do for one edit, in the frames of the day.</summary>
    public static string InstructionFor(EdlEditRecord record)
    {
        EdlSelection s = record.Selection;

        return record.Mode switch
        {
            IngestMode.Blank =>
                $"black video and silent audio from frame {s.FirstFrame:N0} ({s.MarkInTimecode}) for " +
                $"{s.DurationFrames:N0} frames ({s.DurationSeconds:0.###} s), through frame {s.LastFrame:N0}; " +
                "the original frames are not played, and nothing after them moves",
            IngestMode.Cut =>
                $"skip frames {s.FirstFrame:N0} to {s.LastFrame:N0}; the delay shrinks by {s.DurationFrames:N0} frames",
            _ =>
                $"play {Path.GetFileName(record.Media)} ({record.Mode.ToString().ToLowerInvariant()}) from frame " +
                $"{s.FirstFrame:N0} ({s.MarkInTimecode}) for {s.DurationFrames:N0} frames",
        };
    }

    /// <summary>What a change of range does: the frames it adds to the edit, and the original frames it gives back.</summary>
    private static string ChangeOf(EdlSelection before, EdlSelection after, IngestMode mode)
    {
        if (mode != IngestMode.Blank) return "";

        var parts = new List<string>();

        if (after.MarkOut > before.MarkOut)
            parts.Add($"extended: frames {before.MarkOut:N0} to {after.MarkOut - 1:N0} now go out black and silent");
        else if (after.MarkOut < before.MarkOut)
            parts.Add($"shortened: the original frames {after.MarkOut:N0} to {before.MarkOut - 1:N0} ({new Timecode(after.MarkOut, after.Rate)}) are played again");

        if (after.MarkIn < before.MarkIn)
            parts.Add($"frames {after.MarkIn:N0} to {before.MarkIn - 1:N0} now go out black and silent");
        else if (after.MarkIn > before.MarkIn)
            parts.Add($"the original frames {before.MarkIn:N0} to {after.MarkIn - 1:N0} ({new Timecode(before.MarkIn, before.Rate)}) are played again");

        return parts.Count > 0 ? "; " + string.Join("; ", parts) : "";
    }

    private static string Summary(IReadOnlyList<LiveCue> cues)
    {
        int blanks = cues.Count(c => c.Mode == IngestMode.Blank);
        int media = cues.Count(c => c.MediaPath is not null);
        int cuts = cues.Count(c => c.Mode == IngestMode.Cut);

        var parts = new List<string>();
        if (blanks > 0) parts.Add($"{blanks} deleted scene(s) as black and silence");
        if (media > 0) parts.Add($"{media} ingest item(s)");
        if (cuts > 0) parts.Add($"{cuts} scene cut(s)");

        return parts.Count > 0 ? string.Join(", ", parts) : "no edits";
    }
}
