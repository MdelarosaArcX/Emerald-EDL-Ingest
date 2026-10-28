using System.Windows;
using Emerald.Core;

namespace Emerald.LiveEdit;

/// <summary>
/// Undo and redo for the timeline's edits: what is placed on the ingest lane and the layers,
/// deleted scenes, levels, and the audio lane's cuts and levels.
///
/// Each edit records the whole edit state as it was before it - small, a list of placements and
/// levels - and undo puts that back. Whole states rather than inverse operations, because an
/// edit here can touch several things at once (a placement and the cues it makes) and a state
/// put back cannot get half of that wrong.
///
/// <b>Air first.</b> An undo or redo that would change anything air has reached, or is about to,
/// is refused, exactly as the edit itself would be: what has gone out has gone out, and what is
/// in the no edit zone is already open on the transmitter.
/// </summary>
public partial class LiveEditWindow
{
    /// <summary>How many edits can be undone, and redone. The oldest is forgotten past this.</summary>
    private const int UndoLimit = 50;

    private readonly record struct OverlayState(Overlay Overlay, long StartFrame, double GainDb, IngestPlan? Plan);

    private sealed record EditState(
        string What,
        OverlayState[] Overlays,
        double[] LaneGainDb,
        IReadOnlyList<AudioEdit> Audio,
        long[] AudioCuts);

    private readonly List<EditState> _undo = new();
    private readonly List<EditState> _redo = new();

    /// <summary>
    /// What the last remembered edit was, so a run of the same small one - a level pressed ten
    /// times - is undone in one go rather than ten.
    /// </summary>
    private string? _rememberedKey;
    private DateTime _rememberedAt;

    private EditState Capture(string what) => new(
        what,
        _overlays.Select(o => new OverlayState(o, o.StartFrame, o.GainDb, o.Plan)).ToArray(),
        (double[])_laneGainDb.Clone(),
        _audioEdits,
        _audioCuts.ToArray());

    /// <summary>
    /// Called by every edit just before it changes anything - after it has passed its checks, so
    /// a refused edit leaves nothing to undo.
    /// </summary>
    /// <param name="key">The same key within two seconds of the last is the same edit continuing.</param>
    private void Remember(string what, string? key = null)
    {
        DateTime now = DateTime.UtcNow;

        bool continuing = key is not null && key == _rememberedKey && (now - _rememberedAt).TotalSeconds < 2;

        _rememberedKey = key;
        _rememberedAt = now;

        if (continuing) return;

        Push(_undo, Capture(what));
        _redo.Clear();

        ShowUndo();
    }

    private static void Push(List<EditState> stack, EditState state)
    {
        stack.Add(state);
        if (stack.Count > UndoLimit) stack.RemoveAt(0);
    }

    /// <summary>Forgets everything: the states held no longer describe the timeline, as when the day moves.</summary>
    private void ForgetEdits()
    {
        _undo.Clear();
        _redo.Clear();
        _rememberedKey = null;
        ShowUndo();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo() => Step(_undo, _redo, "UNDO");
    private void Redo() => Step(_redo, _undo, "REDO");

    private void Step(List<EditState> from, List<EditState> to, string verb)
    {
        if (from.Count == 0)
        {
            LayerText.Text = verb == "UNDO" ? "Nothing to undo." : "Nothing to redo.";
            LayerText.Foreground = Brush("Muted");
            return;
        }

        EditState target = from[^1];

        if (ChangedFrom(target) is { } at && !Editable(at))
        {
            Refuse($"NO EDIT ZONE - {verb.ToLowerInvariant()} of {target.What} would change air at " +
                   $"{Stamp(at)}, which has gone out or is about to.");
            return;
        }

        from.RemoveAt(from.Count - 1);
        Push(to, Capture(target.What));

        Restore(target);
        _rememberedKey = null;

        ActivityLog.Shared.Info(LogSource.LiveEdit,
            $"{verb}  {target.What} - {_undo.Count} to undo, {_redo.Count} to redo.",
            @event: verb == "UNDO" ? "liveedit.undo" : "liveedit.redo");

        LayerText.Text = $"{verb}  {target.What}";
        LayerText.Foreground = Brush("Tc");

        ShowUndo();
    }

    /// <summary>
    /// The earliest point on the ruler where putting <paramref name="target"/> back would change
    /// what goes to air, or null when it changes nothing that does - a hand-dropped layer, a level
    /// on one, a cut in the sound. Those are this window's monitor, not the transmitter's.
    /// </summary>
    private long? ChangedFrom(EditState target)
    {
        List<IngestItem> items = IngestItems();
        long? earliest = null;

        void Consider(Overlay overlay, IngestPlan plan)
        {
            long at = IngestTimeline.RulerStart(new IngestItem(overlay.Path, plan), items);
            earliest = earliest is { } e ? Math.Min(e, at) : at;
        }

        foreach (OverlayState state in target.Overlays)
        {
            if (state.Plan is not { } plan) continue;

            Overlay? now = _overlays.FirstOrDefault(o => ReferenceEquals(o, state.Overlay));

            if (now is null || !Equals(now.Plan, state.Plan))
            {
                Consider(state.Overlay, plan);
                if (now?.Plan is { } current) Consider(now, current);
            }
        }

        foreach (Overlay overlay in _overlays)
            if (overlay.Plan is { } plan && !target.Overlays.Any(s => ReferenceEquals(s.Overlay, overlay)))
                Consider(overlay, plan);

        if (AudioEdits.FirstDifference(_audioEdits, target.Audio) is { } frame)
        {
            long at = RulerOf(frame);
            earliest = earliest is { } e ? Math.Min(e, at) : at;
        }

        return earliest;
    }

    private void Restore(EditState state)
    {
        // A layer item going away may be the one sounding.
        for (int lane = FirstDropLane; lane < AllLanes; lane++)
            if (_onLane[lane] is { } playing && !state.Overlays.Any(s => ReferenceEquals(s.Overlay, playing)))
            {
                LanePlayer(lane)?.Stop();
                _onLane[lane] = null;
            }

        _overlays.Clear();

        foreach (OverlayState s in state.Overlays)
        {
            s.Overlay.StartFrame = s.StartFrame;
            s.Overlay.GainDb = s.GainDb;
            s.Overlay.Plan = s.Plan;
            _overlays.Add(s.Overlay);
        }

        Array.Copy(state.LaneGainDb, _laneGainDb, _laneGainDb.Length);

        _audioEdits = state.Audio;
        _audioCuts.Clear();
        _audioCuts.UnionWith(state.AudioCuts);

        if (_selected is { } selected && !_overlays.Contains(selected)) _selected = null;
        _audioPiece = null;

        PushCues();
        ApplyLaneLevels();
        FollowAudioLevel(force: true);

        Draw();
        DrawOnAir();
        ShowPosition();
        ShowDelay();
        ShowLayer();
    }

    private void ShowUndo()
    {
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;

        UndoButton.ToolTip = _undo.Count > 0
            ? $"Undo {_undo[^1].What} (Ctrl+Z) - {_undo.Count} of the last {UndoLimit} edits can be undone"
            : $"Undo (Ctrl+Z) - up to the last {UndoLimit} edits";

        RedoButton.ToolTip = _redo.Count > 0
            ? $"Redo {_redo[^1].What} (Ctrl+Y) - {_redo.Count} can be redone"
            : $"Redo (Ctrl+Y) - up to {UndoLimit} undone edits";
    }
}
