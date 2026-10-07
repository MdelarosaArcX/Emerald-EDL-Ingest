using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Emerald.Core;
using Emerald.Media;
using Emerald.Video;

namespace Emerald.LiveEdit;

/// <summary>One captured segment, and where on the station day it sits.</summary>
internal sealed record Placed(CapturedClip Clip, long StartFrame, long Frames)
{
    public long EndFrame => StartFrame + Frames;

    public bool Holds(long dayFrame) => dayFrame >= StartFrame && dayFrame < EndFrame;

    /// <summary>How far into this clip a day frame is.</summary>
    public long OffsetOf(long dayFrame) => Math.Clamp(dayFrame - StartFrame, 0, Math.Max(0, Frames - 1));

    /// <summary>True while the recorder still has it open, so it cannot be played yet.</summary>
    public bool Pending => Clip.StillBeingWritten;
}

/// <summary>
/// The editing workspace: a viewer, a transport, and a timeline that is the <b>station day</b>.
///
/// <b>The timeline is the clock, not the clip.</b> A segment that began at 11:34:52 is drawn at
/// 11:34:52, the next one begins where it ends, and the whole recording reads across the day as
/// one continuous thing made of many files. That is what makes the on-air marker meaningful: the
/// transmitter is carrying what the receiver produced a delay ago, so on air is a place on this
/// same ruler and can be pointed at rather than worked out.
///
/// Everything counts in frames of the day. A position is a frame from midnight; which file it
/// falls in is a lookup. That keeps one coordinate for the media, the playhead, the marks and
/// the transmitter, which is the only way they can be compared at all.
///
/// <b>Where a segment's start comes from.</b> Segments carry no timecode track — one encoder
/// writes them all, and a <c>-timecode</c> would stamp every one with the same start. So a
/// recording writes into a folder named for the moment it rolled, and its segments are numbered
/// inside it: a segment begins where the one before it ended, which is exact. Recordings made
/// before that scheme carry a timestamp in the filename instead, written to the second, and are
/// therefore up to a second out — they are still shown, because a plant's existing recordings do
/// not stop mattering when the naming changes.
///
/// Only <b>one</b> recording is laid out, and the CAPTURED MEDIA panel lists that one rather than
/// the whole store: the folder on air if there is one, and otherwise the newest. Two recordings
/// can cover the same minutes, and on a plant that restarts a capture while the delay is still
/// playing out the one before it they do — overlapping them on one ruler would have the marker
/// pointing into a pile rather than at a file, and the newest folder would be the wrong one.
///
/// The viewer plays the proxy: an H.264 file scrubs where the DNxHD master will not.
/// </summary>
public partial class LiveEditWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly TimecodeService _timecode = TimecodeLink.Service;

    private readonly int _rate;
    private readonly long _dayFrames;

    private readonly IReadOnlyList<ZoomLevel> _ladder;
    private int _zoom;

    /// <summary>Every segment of the day being shown, in order, with its place on the clock.</summary>
    private List<Placed> _placed = new();

    /// <summary>
    /// The recording folder everything that needs <i>one</i> recording is about: the player, the
    /// frame counts, the infinite view.
    ///
    /// The ruler shows every file of the day, but "how far into the recording am I" and "what is
    /// under the playhead" are questions about a single recording, and on a day with two
    /// overlapping captures there has to be an answer to which. It is the one on air when
    /// something is, and otherwise the newest.
    /// </summary>
    private string? _primaryFolder;

    /// <summary>The segments of the recording in question, which is the subject of the counts.</summary>
    private IEnumerable<Placed> Primary =>
        _primaryFolder is null
            ? _placed
            : _placed.Where(p => MediaLibrary.RecordingFolder(p.Clip.Path) == _primaryFolder);

    /// <summary>The day on the ruler. The store can hold several, and one ruler holds one.</summary>
    private DateTime _day = DateTime.Today;

    /// <summary>The clip loaded in the player, and where it sits. Null when the playhead is in a gap.</summary>
    private Placed? _loaded;

    /// <summary>The playhead, as a frame of the day. Everything else is drawn from it.</summary>
    private long _at;

    private long? _markIn, _markOut;
    private bool _playing;

    /// <summary>
    /// Where the visible window starts, in frames of the day.
    ///
    /// Held rather than derived from the playhead, because the operator can now scroll. It
    /// follows the playhead when the playhead would otherwise leave the window, and stays where
    /// it was put the rest of the time — a view that snapped back to centre every frame would
    /// make the scrollbar useless while anything was playing.
    /// </summary>
    private long _scroll;

    /// <summary>Frames into the loaded clip that the player has been asked to sit at.</summary>
    private long _wantOffset;

    /// <summary>
    /// True once the player has actually opened the current source.
    ///
    /// Setting <c>Position</c> before that does nothing at all: the media is not open, the value
    /// is discarded, and playback then starts from the beginning of the file — which is exactly
    /// the bug where clicking 18:31:23:24 and pressing space played from 18:30:00:00. The wanted
    /// offset is remembered and applied when the open arrives.
    /// </summary>
    private bool _open;

    private long Span => _ladder[_zoom].SpanFrames;

    /// <summary>
    /// How far the recording in question runs, first segment to last, or zero if there is none.
    ///
    /// Grows by itself: the segment being written is in the layout at its assumed length, and a
    /// new one appearing moves the end. So a view fitted to this follows a running recording
    /// without being told to.
    /// </summary>
    private long RecordingExtent
    {
        get
        {
            List<Placed> of = Primary.ToList();

            return of.Count == 0 ? 0 : of.Max(p => p.EndFrame) - of.Min(p => p.StartFrame);
        }
    }

    /// <summary>
    /// The window actually on screen — the one every x-position is worked out from.
    ///
    /// Normally the zoom rung and the scroll position. In the infinite view it is the recording
    /// itself: first frame to last, fitted exactly to the canvas, so the whole of it is on screen
    /// and none of the screen is spent on hours of empty day either side. Zoom and scroll are
    /// left untouched underneath, so switching back returns to where the operator was.
    ///
    /// Everything that draws or hit-tests goes through this pair. They used to read _scroll and
    /// Span directly in a dozen places, which is exactly how a view ends up with the playhead
    /// computed against one window and the blocks against another.
    /// </summary>
    private long ViewStart => _infinite && RecordingExtent > 0 ? RecordingStartFrame : _scroll;

    private long ViewSpan
    {
        get
        {
            if (!_infinite) return Span;

            long extent = RecordingExtent;

            // A pad of a fiftieth so the first and last blocks are not flush against the edges
            // of the canvas, which reads as "cut off" rather than "this is all of it".
            return extent <= 0 ? Span : extent + Math.Max(1, extent / 50);
        }
    }

    /// <summary>Keeps the window somewhere sensible after a zoom, and never past the end of the day.</summary>
    private void ClampScroll() =>
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _dayFrames - Span));

    /// <summary>
    /// Brings the playhead into view if it is not, and otherwise leaves the window alone.
    ///
    /// Centring is for when it has gone off the edge, which is a jump the operator is expecting
    /// because they caused it. Nudging the view every frame while playing would fight the
    /// scrollbar.
    /// </summary>
    private void EnsureVisible(long at)
    {
        if (Span >= _dayFrames) { _scroll = 0; return; }

        if (at < _scroll || at >= _scroll + Span) _scroll = at - Span / 2;

        ClampScroll();
    }

    /// <summary>
    /// Shows the scrollbar when there is something to scroll, and hides it at the widest zoom
    /// where the whole day is already across the window.
    /// </summary>
    private void ShowScroll()
    {
        // Nothing to scroll in the infinite view either: the whole recording is fitted to the
        // canvas by definition, so a scrollbar there would be a control that cannot move.
        bool needed = !_infinite && Span < _dayFrames;

        TimeScroll.Visibility = needed ? Visibility.Visible : Visibility.Collapsed;
        if (!needed) return;

        // Set together and in this order: Maximum before Value, or WPF clamps the value against
        // the old maximum and the thumb lands in the wrong place after a zoom out.
        //
        // The thumb is sized ViewportSize/(Maximum+ViewportSize), so at full zoom - twenty frames
        // out of a day - it comes to four thousandths of the track and cannot be grabbed. The
        // viewport is therefore floored at a fiftieth of the day purely so there is something to
        // take hold of. It does not change what can be scrolled to: Value still runs the whole
        // range, and Value is what the window is drawn from.
        TimeScroll.Maximum = _dayFrames - Span;
        TimeScroll.ViewportSize = Math.Max(Span, _dayFrames / 50.0);
        TimeScroll.LargeChange = Span;
        TimeScroll.SmallChange = Math.Max(1, Span / 20);
        TimeScroll.Value = _scroll;
    }

    private void TimeScroll_Scroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e)
    {
        _scroll = (long)Math.Round(e.NewValue);
        ClampScroll();

        // The playhead is not moved. Scrolling is looking somewhere else, not going there.
        Draw();
        DrawOnAir();
    }

    /// <summary>Redraws while playing, and moves the on-air marker whether playing or not.</summary>
    /// <summary>
    /// Moves the markers. <b>Normal</b> priority, not Render: at Render it outranks input and the
    /// media pipeline's own work, which is how a timer meant to draw a line ended up competing
    /// with the thing it was drawing a line about.
    /// </summary>
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Normal);

    public LiveEditWindow()
    {
        InitializeComponent();

        _rate = _settings.CaptureFrameRate > 0 ? _settings.CaptureFrameRate : 25;
        _dayFrames = TimelineZoom.DayFrames(_rate);

        _ladder = TimelineZoom.Ladder(_rate);
        _zoom = TimelineZoom.DefaultIndex(_rate);

        _tick.Interval = TimeSpan.FromMilliseconds(1000.0 / _rate);
        _tick.Tick += (_, _) => OnTick();

        _monitorTick.Tick += (_, _) => OnMonitorTick();

        Loaded += (_, _) =>
        {
            LoadBookmarks();
            LoadClips();
            ShowZoom();
            ShowPosition();
            ApplyLevel();
            ShowLayer();

            // The on-air marker has to move whether anything is playing or not, so the timer
            // runs for the life of the window rather than only during playback.
            _tick.Start();

            _watch.Tick += (_, _) => CheckStore();
            _watch.Start();

            Focus();
        };

        Closing += LiveEditWindow_Closing;
    }


    // ------------------------------------------------------------------ keeping up with the store

    /// <summary>Looks for new segments. Cheap unless something has actually changed.</summary>
    private readonly DispatcherTimer _watch =
        new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };

    /// <summary>What the store looked like last time, so an unchanged store costs nothing.</summary>
    private string _signature = "";

    private bool _listing;

    /// <summary>
    /// Set while the clip list is being rebound after a refresh.
    ///
    /// Restoring the selection raises SelectionChanged, and that handler moves the playhead to
    /// the clip's start — so without this a segment closing every two minutes would yank the
    /// playhead back to the top of whatever was selected, mid-playback.
    /// </summary>
    private bool _rebinding;

    /// <summary>False until the first listing has placed something, so the view can open on it.</summary>
    private bool _placedOnce;

    /// <summary>
    /// Notices a new segment and puts it on the timeline, without re-probing the store to find out.
    ///
    /// A full listing runs ffprobe once per file — on this plant's own store that is fifty-eight
    /// files and seconds of work — so doing it on a timer would keep a disk busy for no reason.
    /// The timer takes a <see cref="Signature"/> instead and only lists when that has moved.
    /// </summary>
    /// <summary>
    /// Whether the ruler should follow the real date over midnight.
    ///
    /// True until the operator navigates to a particular day themselves. A plant that starts a
    /// delay at 23:50 is still on air at 00:10, and the window opened on yesterday would quietly
    /// stop showing the recording and the marker with it — the media is on the next day's ruler
    /// and there is nothing on this one. So the day rolls under it, unless somebody has gone
    /// looking at a specific day, in which case moving the ruler out from under them would be
    /// worse than leaving it.
    /// </summary>
    private bool _followToday = true;

    /// <summary>
    /// Rolls the ruler onto the new day when midnight passes.
    ///
    /// On the store timer rather than the frame loop: it is a once-a-day event and checking it
    /// twenty-five times a second to catch it a fifth of a second sooner would be absurd.
    /// </summary>
    private void FollowTheDate()
    {
        if (!_followToday) return;

        DateTime today = DateTime.Today;

        if (_day.Date == today) return;

        ActivityLog.Shared.Info(LogSource.App,
            $"Live Edit rolled over midnight: the timeline is now {today:ddd d MMM yyyy}.",
            @event: "liveedit.day");

        GoToDay(today, byHand: false);
    }

    private void CheckStore()
    {
        FollowTheDate();

        if (_listing) return;

        string folder = RecordingProfile.FolderFor(RecordingProfile.LowRes,
                                                  MediaLibrary.FolderFor(_settings));

        string now = Signature(folder);
        if (now == _signature) return;

        _signature = now;
        LoadClips();
    }

    /// <summary>
    /// A cheap fingerprint of the store: which files there are, and how big the closed ones are.
    ///
    /// <b>The file being recorded is deliberately left out.</b> It grows every few hundred
    /// milliseconds, so including its length would make the fingerprint change constantly and
    /// turn this into a full ffprobe pass over the whole store every five seconds — the exact
    /// cost the fingerprint exists to avoid. Its length is not wanted anyway: an open segment is
    /// placed at its assumed length because there is no duration to read.
    ///
    /// What does change the fingerprint is a new segment appearing, and the previously-open one
    /// closing — which is when it stops being the newest, and its final length enters the sum.
    /// That is exactly one refresh per segment, which is once every couple of minutes.
    /// </summary>
    private static string Signature(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return "";

            // Recursively, and that is the fix for a refresh that never happened.
            //
            // This read the top directory only, which was right when segments were written loose
            // into it. They go into a folder per recording now, so the top directory holds
            // nothing but directories - measured on this plant: 0 files here, 25 below - and the
            // fingerprint was the constant "0". It never changed, so the five-second watch never
            // listed the store, and a new segment appeared on the timeline only if somebody
            // pressed Refresh. On air, that is the one thing nobody is watching the screen to do.
            FileInfo[] files = new DirectoryInfo(folder).GetFiles("*", SearchOption.AllDirectories);

            if (files.Length == 0) return "0";

            long newestTicks = files.Max(f => f.LastWriteTimeUtc.Ticks);

            long bytes = 0;
            int counted = 0;

            foreach (FileInfo file in files)
            {
                // Skip the one still being written to.
                if (file.LastWriteTimeUtc.Ticks == newestTicks) continue;

                bytes += file.Length;
                counted++;
            }

            return $"{files.Length}|{counted}|{bytes}";
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }


    // ------------------------------------------------------------------ the store

    private void LoadClips()
    {
        if (_listing) return;
        _listing = true;

        string folder = MediaLibrary.FolderFor(_settings);

        StoreText.Text = $"reading {folder}...";

        // Probing every clip shells out to ffprobe once per file, which is too slow for the UI
        // thread once the store has a few hours in it.
        Task.Run(() => MediaLibrary.List(_settings))
            .ContinueWith(t =>
            {
                _listing = false;

                IReadOnlyList<CapturedClip> clips = t.IsFaulted ? Array.Empty<CapturedClip>() : t.Result;

                // The selection is rebound with the list, so it is remembered across a refresh
                // rather than jumping back to nothing every time a segment closes.
                string? selected = (ClipList.SelectedItem as CapturedClip)?.Path;

                // Placed first, because Place decides which recording is being looked at and the
                // panel shows that one. Listing the whole store beside a timeline showing one
                // folder was two different answers to "what am I looking at".
                Place(clips);

                _rebinding = true;

                // The segment being recorded first, then the rest newest-first.
                //
                // The timeline is laid out on the clock and has to stay that way, but a list is
                // read from the top: what is being written now is what an operator is looking
                // for, and it was at the bottom of the list because it is last on the clock.
                ClipList.ItemsSource = _placed
                    .OrderByDescending(p => p.Pending)
                    .ThenByDescending(p => p.StartFrame)
                    .Select(p => p.Clip)
                    .ToList();

                if (selected is not null)
                    ClipList.SelectedItem = _placed
                        .Select(p => p.Clip)
                        .FirstOrDefault(c => string.Equals(c.Path, selected,
                                                           StringComparison.OrdinalIgnoreCase));

                _rebinding = false;

                StoreText.Text = clips.Count == 0
                    ? $"No captures yet. Recordings land in {folder}."
                    : $"{clips.Count} clip{(clips.Count == 1 ? "" : "s")} in {folder}";

                if (t.IsFaulted)
                    ActivityLog.Shared.Warn(LogSource.App,
                        $"Live Edit could not read the store: {t.Exception?.GetBaseException().Message}",
                        @event: "liveedit.storefailed", file: folder);

            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Lays the day's segments out on the clock.
    ///
    /// Only the day on the ruler. A store holding a week would otherwise pile last Tuesday's
    /// 11:34 on top of today's, and a marker pointing into that pile means nothing. A segment
    /// still being written has no duration to read, so it is given the recorder's segment length
    /// — it is going to be that long, and drawing it as a sliver would hide the very thing an
    /// operator is looking for while a recording is running.
    /// </summary>
    private void Place(IReadOnlyList<CapturedClip> clips)
    {
        long assumed = Math.Max(1, _settings.RecordingSegmentSeconds) * (long)_rate;

        // Every file of the day, from every recording folder.
        //
        // The file view is a view of the store: what is on this machine, laid out on the clock.
        // It used to show one folder - the newest, or the one on air - to avoid drawing two
        // recordings on top of each other where they overlap in time, which they do on a plant
        // that restarts a capture while the delay is still playing out the one before it.
        //
        // Showing one folder solved the overlap by hiding media, which is the wrong trade for a
        // view whose job is to say what is there. The overlap is handled where it belongs
        // instead - in the drawing, where each recording gets a band of its own - and the
        // infinite view is the one that narrows to the recording going to air.
        List<CapturedClip> today = clips
            .Where(c => c.StartsAt is { } at && at.Date == _day.Date)
            .ToList();

        // The folder that is on air, if any, and otherwise the newest.
        //
        // On air wins because that is the recording an operator is watching go out; the newest is
        // the same folder nine times in ten, and the tenth is the case that matters — a capture
        // restarted while the delay is still playing out the one before it. Then the newest folder
        // is not the one on the transmitter, and following the newest would show the wrong media
        // under the marker.
        string? onAirFolder = null;

        if (OnAirFrame() is { } onAir)
            onAirFolder = today
                .Where(c => c.StartsAt is { } s &&
                            TimelineZoom.FrameOfDay(s, _rate) <= onAir &&
                            onAir < TimelineZoom.FrameOfDay(s, _rate) +
                                (c.Info is { } i && i.Duration > TimeSpan.Zero
                                    ? (long)Math.Round(i.Duration.TotalSeconds * _rate)
                                    : assumed))
                .Select(c => MediaLibrary.RecordingFolder(c.Path))
                .FirstOrDefault(f => f is not null);

        string? latest = onAirFolder
            ?? today
                .Select(c => MediaLibrary.RecordingFolder(c.Path))
                .Where(f => f is not null)
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

        // Which recording the player, the infinite view and the frame counts are about. Not a
        // filter any more: everything is laid out, and this one is the subject.
        _primaryFolder = latest;

        int folders = today
            .Select(c => MediaLibrary.RecordingFolder(c.Path) ?? "")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        RecordingText.Text = latest is null
            ? today.Count > 0 ? "recordings from before folders were used" : ""
            : (onAirFolder is not null ? $"recording {latest}  (on air)" : $"recording {latest}")
              + (folders > 1 ? $"   +{folders - 1} more on the ruler" : "");

        // Each clip already knows exactly when it starts: the store works it out folder by
        // folder, numbered segment after numbered segment from the moment the folder says the
        // recording rolled. Accumulating it a second time here would be two answers to one
        // question, and the one that drifted would be this one.
        _placed = today
            .Select(c => new Placed(
                c,
                TimelineZoom.FrameOfDay(c.StartsAt!.Value, _rate),
                c.Info is { } info && info.Duration > TimeSpan.Zero
                    ? (long)Math.Round(info.Duration.TotalSeconds * _rate)
                    : assumed))
            .OrderBy(p => p.StartFrame)
            .ToList();

        DayText.Text = _day.ToString("ddd d MMM yyyy");

        // A refresh rebuilds every Placed, so the one the player is on is a different object
        // afterwards even though it is the same file. Re-bound by path rather than by identity,
        // or a segment closing while something is playing would look like the clip vanishing —
        // and the player would be reloaded from the start under the operator.
        if (_loaded is { } was)
            _loaded = _placed.FirstOrDefault(p =>
                string.Equals(p.Clip.Path, was.Clip.Path, StringComparison.OrdinalIgnoreCase));

        // A fresh window opens where the recording is, not at midnight: an hour-wide view of
        // 00:00 on a store full of this afternoon is a blank timeline and a puzzle.
        if (!_placedOnce && _placed.Count > 0)
        {
            _placedOnce = true;
            Seek(OnAirFrame() ?? _placed[^1].StartFrame);
            return;
        }

        ShowScroll();
        Draw();
        ShowPosition();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadClips();

    private void DayBack_Click(object sender, RoutedEventArgs e) => GoToDay(_day.AddDays(-1));
    private void DayForward_Click(object sender, RoutedEventArgs e) => GoToDay(_day.AddDays(1));

    /// <summary>
    /// Puts the ruler on a day.
    ///
    /// <paramref name="byHand"/> says whether the operator asked for this day in particular.
    /// When they have, the ruler stops following the real date — otherwise somebody looking at
    /// last Tuesday at one minute to midnight would be moved off it. Going back to today by hand
    /// resumes the following, because that is plainly what it means.
    /// </summary>
    private void GoToDay(DateTime day, bool byHand = true)
    {
        Pause();

        if (byHand) _followToday = day.Date == DateTime.Today;

        // The bookmarks of the day being left, in case one was typed and not yet written.
        SaveBookmarks();

        // Forgotten, so the store is listed again for the new day even if no segment has closed.
        // Without this a rollover past midnight could leave the ruler on a day whose clips were
        // never read, waiting for a fingerprint change that had already happened.
        _signature = "";

        _day = day;
        _loaded = null;
        Player.Source = null;
        _markIn = _markOut = null;

        LoadBookmarks();
        LoadClips();
    }

    /// <summary>
    /// Picking a clip from the list previews it in the small monitor, and leaves the timeline
    /// exactly where it was.
    ///
    /// It used to move the playhead there, which meant "is this the right file" cost the operator
    /// whatever they had lined up — and loaded it into the big screen, which belongs to the
    /// timeline. Double-click is the gesture for "take me to it", below.
    /// </summary>
    private void ClipList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebinding) return;
        if (ClipList.SelectedItem is not CapturedClip clip) return;

        ShowInMonitor(clip.Path, clip.DisplayName);
    }

    /// <summary>
    /// Double-click takes the timeline to the clip, which is what single-click used to do.
    ///
    /// Kept, because jumping to a segment by name is genuinely useful on a day with thirty of
    /// them — it just should not be what happens when somebody is only looking.
    /// </summary>
    private void ClipList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ClipList.SelectedItem is not CapturedClip clip) return;

        if (clip.StartsAt is not { } at)
        {
            StoreText.Text = $"{clip.Name} has no timestamp, so it has no place on the day.";
            return;
        }

        // A clip from another day moves the ruler to that day first.
        if (at.Date != _day.Date) GoToDay(at.Date);

        Pause();
        Seek(TimelineZoom.FrameOfDay(at, _rate));
    }

    // ------------------------------------------------------------------ the player

    /// <summary>
    /// Which segment is under a frame of the day.
    ///
    /// The recording in question wins where two overlap. Without that, the player would load
    /// whichever happened to sort first — an older capture covering the same minutes — and the
    /// operator would be watching the wrong feed with no way to tell.
    /// </summary>
    private Placed? ClipAt(long dayFrame) =>
        Primary.FirstOrDefault(p => p.Holds(dayFrame))
        ?? _placed.FirstOrDefault(p => p.Holds(dayFrame));

    /// <summary>
    /// The closest segment that can actually be played, or none.
    ///
    /// Closest by the gap between the frame and the segment, so a playhead sitting in a hole
    /// between two recordings goes to whichever side it is nearer. The segment still being
    /// written is excluded: it has no index yet and will not open.
    /// </summary>
    private Placed? NearestPlayable(long dayFrame) => _placed
        .Where(p => !p.Pending)
        .OrderBy(p => p.Holds(dayFrame)
            ? 0
            : Math.Min(Math.Abs(p.StartFrame - dayFrame), Math.Abs(p.EndFrame - 1 - dayFrame)))
        .FirstOrDefault();

    /// <summary>
    /// Puts the playhead at a frame of the day, loading whichever segment is there.
    ///
    /// The seam between two files is crossed by loading the next one and starting it at the
    /// offset the day frame implies, so scrubbing across a segment boundary behaves as though
    /// the recording were one file — which, as far as the operator is concerned, it is.
    /// </summary>
    private void Seek(long dayFrame)
    {
        _at = Math.Clamp(dayFrame, 0, _dayFrames - 1);

        Placed? want = ClipAt(_at);

        if (want is null || want.Pending)
        {
            // Nothing playable here: a gap between recordings, or the segment the recorder still
            // has open. Said rather than left as a blank picture with no explanation.
            if (_loaded is not null) { Player.Stop(); Player.Source = null; _loaded = null; }

            PreviewPlaceholder.Text = want is null
                ? $"Nothing recorded at {Stamp(_at)}"
                : $"{want.Clip.Name}\n\nstill being recorded - it cannot be played until the " +
                  $"recorder closes it, about every {_settings.RecordingSegmentSeconds / 60} min";

            PreviewPlaceholder.Visibility = Visibility.Visible;

            Draw();
            ShowPosition();
            return;
        }

        _wantOffset = want.OffsetOf(_at);

        if (!ReferenceEquals(_loaded, want))
        {
            _loaded = want;
            _open = false;

            Player.Source = new Uri(want.Clip.Path);
            PreviewPlaceholder.Visibility = Visibility.Collapsed;

            ActivityLog.Shared.Info(LogSource.App,
                $"LOAD  {want.Clip.Name} - in Live Edit at {Stamp(want.StartFrame)}, " +
                $"{want.Clip.SizeText}, from {System.IO.Path.GetDirectoryName(want.Clip.Path)}.",
                @event: "liveedit.loaded", file: want.Clip.Path);
        }

        // Only if the media is actually open. Otherwise the offset waits for Player_Opened,
        // because a Position set against an unopened source is silently thrown away.
        if (_open) ApplyOffset();

        EnsureVisible(_at);
        ShowScroll();

        Draw();
        DrawOnAir();
        ShowPosition();
    }

    /// <summary>
    /// Puts the player at the offset the day frame asked for.
    ///
    /// The Play/Pause pair is what makes a paused player show the frame it is parked on — with
    /// ScrubbingEnabled it decodes and holds it rather than staying black. It is done after the
    /// seek, never before, or it starts from wherever the file happened to be.
    /// </summary>
    private void ApplyOffset()
    {
        TimeSpan want = TimeSpan.FromSeconds(_wantOffset / (double)_rate);

        Player.Position = want;

        if (!_playing) { Player.Play(); Player.Pause(); }

        // Checked afterwards, because a seek that did not take is the whole of the complaint
        // that clicking the timeline played from the start of a segment.
        //
        // Two separate things can swallow it. A position set from inside MediaOpened is applied
        // before the engine will accept one on some sources, and is dropped without a word. And
        // a player can only land on a keyframe, so even an accepted seek lands at the one at or
        // before the frame asked for - measured on this plant, a 120-second proxy held eleven
        // keyframes, so that is up to eleven seconds early on older media.
        //
        // The first is worth retrying and the second is not, so they are told apart by how far
        // out the landing is, on the next dispatcher pass once the engine has settled.
        VerifyOffset(want);
    }

    /// <summary>How many times the current seek has been re-applied, to stop a retry loop.</summary>
    private int _seekRetries;

    /// <summary>
    /// Confirms the player went where it was sent, and re-sends it once if it plainly did not.
    ///
    /// Scheduled rather than immediate: the position cannot be read back usefully in the same
    /// pass that set it. Background priority so it never competes with the frame loop.
    /// </summary>
    private void VerifyOffset(TimeSpan want) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_loaded is null || !_open) return;

            // Only while parked. Once it is playing the position has moved on by design, and by
            // the time a background callback runs it could legitimately be seconds past where
            // the seek put it - which would read as a failed seek and re-send it, jerking the
            // picture back mid-playback.
            if (_playing) return;

            TimeSpan at = Player.Position;
            double outBy = Math.Abs((at - want).TotalSeconds);

            // Within a GOP of where it was asked for: the engine took the seek and landed on the
            // nearest keyframe, which is as close as a player can get. Nothing to do.
            if (outBy < 1.5)
            {
                _seekRetries = 0;
                return;
            }

            // Further out than any GOP, and sitting at the top of the file: the seek was
            // discarded rather than rounded. Send it again, once.
            if (_seekRetries == 0 && at < TimeSpan.FromSeconds(1))
            {
                _seekRetries++;

                Player.Position = want;
                if (!_playing) { Player.Play(); Player.Pause(); }

                VerifyOffset(want);
                return;
            }

            _seekRetries = 0;

            // Still not there. Said out loud rather than left as a picture at the wrong time,
            // because the operator is about to mark an edit against what they can see.
            StoreText.Text =
                $"The player landed at {new Timecode((long)Math.Round(at.TotalSeconds * _rate), _rate)} " +
                $"into this segment, not {new Timecode(_wantOffset, _rate)} - it can only seek to a " +
                "keyframe, and this file has few. Recordings made from now on carry one every half second.";

        }, DispatcherPriority.Background);

    private void Player_Opened(object sender, RoutedEventArgs e)
    {
        // The first moment the position can be set on this source, and therefore the moment the
        // frame the operator clicked is actually reached rather than the start of the file.
        _open = true;
        _seekRetries = 0;

        ApplyOffset();

        // It was opened in order to play, so it plays. Without this, crossing into a new segment
        // mid-playback would load the file and then sit there.
        if (_playing) Player.Play();

        Draw();
        ShowPosition();
    }

    /// <summary>The end of one segment is the start of the next, so playout runs straight on.</summary>
    private void Player_Ended(object sender, RoutedEventArgs e)
    {
        if (_loaded is not { } was) { Pause(); return; }

        long next = was.EndFrame;

        if (ClipAt(next) is { Pending: false })
        {
            Seek(next);
            if (_playing) Player.Play();
            return;
        }

        Pause();
        Seek(next);
    }

    private void Player_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        Pause();

        PreviewPlaceholder.Text = $"{_loaded?.Clip.Name}\n\ncould not be played";
        PreviewPlaceholder.Visibility = Visibility.Visible;

        ActivityLog.Shared.Error(LogSource.App,
            $"Live Edit could not play {_loaded?.Clip.Name}: {e.ErrorException?.Message}",
            @event: "liveedit.playfailed", file: _loaded?.Clip.Path);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => PlayPause();

    private void PlayPause()
    {
        if (_playing) { Pause(); return; }

        // Nothing under the playhead: go to the nearest thing that can be played and play that.
        //
        // This used to return silently, and after list selections stopped loading the big screen
        // that became the ordinary case - a fresh window has the playhead wherever it opened,
        // often in a gap, so the space bar did nothing at all and looked broken. Space means
        // "play", and the only honest answers are to play something or to say why not.
        if (_loaded is null)
        {
            if (NearestPlayable(_at) is not { } target)
            {
                StoreText.Text = _placed.Count == 0
                    ? "Nothing recorded on this day to play."
                    : "The only segment here is still being recorded - it cannot be played until " +
                      "the recorder closes it.";
                return;
            }

            Seek(target.Holds(_at) ? _at : target.StartFrame);

            // Seek loads the segment under the playhead. If it still could not, say so rather
            // than falling through and playing nothing.
            if (_loaded is null) return;
        }

        Player.Play();
        _playing = true;
        PlayButton.Content = "⏸";

        ActivityLog.Shared.Info(LogSource.App,
            $"PLAY  {_loaded.Clip.Name} - from {Stamp(_at)} in Live Edit.",
            @event: "liveedit.play", file: _loaded.Clip.Path);
    }

    private void Pause()
    {
        if (!_playing) return;

        Player.Pause();
        StopLayers();
        _playing = false;
        PlayButton.Content = "▶";

        ActivityLog.Shared.Info(LogSource.App,
            $"PAUSE {_loaded?.Clip.Name} - at {Stamp(_at)} in Live Edit.",
            @event: "liveedit.pause", file: _loaded?.Clip.Path);
    }

    // ------------------------------------------------------------------ the clock

    /// <summary>
    /// Every frame: the playhead if something is playing, and the on-air marker always.
    ///
    /// <b>This must stay cheap.</b> It used to call <see cref="Draw"/>, which clears both canvases
    /// and rebuilds every ruler tick, label, clip block, name and frame count — twenty-five times
    /// a second, on the UI thread, at a dispatcher priority above input. That is what made
    /// playback stutter: the dispatcher had no time left for anything else, the player included.
    ///
    /// Nothing on the timeline moves while playing except two vertical lines, and both are drawn
    /// as a margin on a rectangle that already exists. So the frame loop moves two margins and
    /// writes one string, and the timeline itself is only rebuilt when its <i>shape</i> changes —
    /// a zoom, a scroll, a new clip, a mark.
    /// </summary>
    private void OnTick()
    {
        if (_playing && _loaded is { } loaded)
        {
            _at = loaded.StartFrame + (long)Math.Round(Player.Position.TotalSeconds * _rate);

            if (_markOut is { } outAt && _at >= outAt)
            {
                Pause();
                Seek(outAt);
                return;
            }

            // Only when the playhead would otherwise leave the window. Scrolling the view is a
            // rebuild, so it must not happen every frame.
            if (_at < _scroll || _at >= _scroll + Span)
            {
                EnsureVisible(_at);
                ShowScroll();
                Draw();
            }
        }

        FollowLayers();
        DrawMarkers();
    }

    /// <summary>
    /// The two moving parts, and nothing else: where the playhead is, where the transmitter is,
    /// and the clock readout. No visuals are created and nothing is laid out again.
    /// </summary>
    private void DrawMarkers()
    {
        double width = TrackCanvas.ActualWidth;

        if (width > 1)
        {
            Playhead.Margin = new Thickness(
                TimelineZoom.XOf(_at, ViewStart, ViewSpan, width) ?? -10, 0, 0, 0);
        }

        PositionText.Text = Stamp(_at);
        ShowFrames();

        DrawOnAir();
    }

    /// <summary>
    /// The position as a frame count: where it is in the day, and where it is in the clip loaded
    /// under it. Both, because an edit is cut at a frame of a file and logged at a frame of the
    /// day, and doing that conversion by hand is how an edit lands one frame out.
    /// </summary>
    /// <summary>
    /// Where the recording in question starts, and how long it is in frames.
    ///
    /// The recording, not the ruler. With every folder of the day laid out, summing the lot would
    /// make "frame 400 of 90,000" mean nothing — two overlapping captures would add up to more
    /// frames than there were minutes.
    /// </summary>
    private long RecordingStartFrame =>
        Primary.Select(p => p.StartFrame).DefaultIfEmpty(0).Min();

    private long RecordingFrames => Primary.Sum(p => p.Frames);

    /// <summary>
    /// The position as a frame count, and the total <b>of this recording</b>.
    ///
    /// Counted from the start of the folder on the ruler rather than from midnight. A frame of the
    /// day is a seven-figure number that means nothing on its own — "frame 1,234,567" tells an
    /// operator only that it is the afternoon. What they are editing is one recording, so the
    /// count that helps is how far into it they are and how long it is.
    /// </summary>
    private void ShowFrames()
    {
        if (_placed.Count == 0)
        {
            FramesText.Text = "";
            return;
        }

        long into = Math.Max(0, _at - RecordingStartFrame) + 1;
        long total = RecordingFrames;

        FramesText.Text = _loaded is { } loaded
            ? $"f {into:N0}/{total:N0}   clip {loaded.OffsetOf(_at) + 1:N0}/{loaded.Frames:N0}"
            : $"f {into:N0}/{total:N0}";
    }

    /// <summary>
    /// Where the transmitter is, on this ruler.
    ///
    /// Only while tidal lock is actually carrying the receiver. The EDL's own playout is files
    /// cued against the clock rather than the recording running late, so there is no position on
    /// a timeline of captured media that would mean anything for it.
    /// </summary>
    private long? OnAirFrame()
    {
        TidalLock tidal = TidalLock.Shared;

        if (tidal.State is not (TidalLockState.OnAir or TidalLockState.Draining)) return null;
        if (!_timecode.TryGetCurrent(out Timecode now)) return null;

        // Through seconds rather than frames, because the station clock can run at a different
        // rate from the one this deck counts in and frames are not comparable across rates.
        return TimelineZoom.OnAirFrame(TimeSpan.FromSeconds(now.TotalSeconds), tidal.Delay, _rate);
    }

    private void DrawOnAir()
    {
        double width = TrackCanvas.ActualWidth;

        if (OnAirFrame() is not { } onAir || width <= 1)
        {
            OnAirMarker.Visibility = Visibility.Collapsed;

            // Said rather than left blank. An absent marker and a marker that has stopped look
            // identical, and the two reasons it can be absent are both things the operator can
            // act on - arm the lock, or fix the clock.
            OnAirText.Text = TidalLock.Shared.State switch
            {
                TidalLockState.Off => "no on-air mark - tidal lock is off",
                TidalLockState.Armed => "no on-air mark - armed, nothing recording yet",
                TidalLockState.CountingDown => "no on-air mark - the delay is still filling",
                _ when !_timecode.TryGetCurrent(out _) => "no on-air mark - no station clock",
                _ => "",
            };

            OnAirText.Foreground = Brush("Muted");
            return;
        }

        OnAirText.Foreground = Brush("Bad");

        OnAirText.Text = $"ON AIR {Stamp(onAir)}";

        if (TimelineZoom.XOf(onAir, ViewStart, ViewSpan, width) is { } x)
        {
            OnAirMarker.Margin = new Thickness(x, 0, 0, 0);
            OnAirMarker.Visibility = Visibility.Visible;
        }
        else
        {
            // Off the window. The readout still says where it is, so the operator can zoom out
            // to it rather than wonder whether it has stopped.
            OnAirMarker.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Puts the playhead on the transmitter, which is the reason for the marker.</summary>
    private void GoToOnAir()
    {
        if (OnAirFrame() is not { } onAir) return;

        Pause();
        Seek(onAir);
    }


    // ------------------------------------------------------------------ level

    /// <summary>
    /// How loud this window plays, and whether it is silenced.
    ///
    /// <b>A monitor control and nothing more.</b> It moves
    /// <see cref="System.Windows.Controls.MediaElement.Volume"/>, which is what comes out of
    /// this PC's speakers — it changes nothing that was recorded, nothing embedded in a file,
    /// and nothing on the transmitter. Emerald has a second, entirely different kind of level:
    /// the EDL's per-track gain, which does go to air. Confusing the two is the mistake this
    /// comment exists to prevent, so the readout says "preview".
    ///
    /// Shown in decibels because that is what an operator reads a level in, and the slider is
    /// linear amplitude — 0.5 on the slider is -6 dB, not half as loud.
    /// </summary>
    private void Level_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LevelText is null) return;

        ApplyLevel();
    }

    private void Mute_Changed(object sender, RoutedEventArgs e) => ApplyLevel();

    private void ApplyLevel()
    {
        bool muted = MuteButton.IsChecked == true;
        double level = LevelSlider.Value;

        Player.IsMuted = muted;
        Player.Volume = level;

        LevelText.Text = muted ? "muted"
            : level <= 0.0001 ? "-inf dB"
            : $"{20 * Math.Log10(level):+0.0;-0.0;0.0} dB";

        LevelText.Foreground = Brush(muted ? "Muted" : "Tc");

        ApplyLaneLevels();
    }

    private Brush Brush(string key) => (Brush)FindResource(key);



    // ------------------------------------------------------------------ the whole recording

    /// <summary>
    /// Whether the timeline is the recording fitted to the screen, rather than the clock.
    ///
    /// The ordinary view is the clock: a block per segment, in its place, zoomable to a frame.
    /// That is what an edit is made on. It is the wrong view for "how much is there" — thirty
    /// two-minute blocks across an hour tell you about the segmenting and nothing about the
    /// recording. So this collapses them into the one thing they actually are, and fits the view
    /// to exactly that span: the first segment at the left edge, the last at the right, nothing
    /// spent on the empty hours either side.
    /// </summary>
    private bool _infinite;

    private void Infinite_Changed(object sender, RoutedEventArgs e)
    {
        _infinite = InfiniteButton.IsChecked == true;

        // Nothing to set up. The view fits itself to the recording through ViewStart/ViewSpan for
        // as long as this is on, and the zoom and scroll underneath are left exactly as they
        // were - so turning it off puts the operator back where they were rather than at
        // midnight on the widest rung, which is where the first version of this dumped them.

        ShowZoom();
        ShowScroll();
        Draw();
        DrawOnAir();

        ActivityLog.Shared.Info(LogSource.App,
            _infinite
                ? "Live Edit timeline set to infinite - the whole recording as one bar."
                : "Live Edit timeline set to normal - a block per segment on the clock.",
            @event: "liveedit.timeline");
    }

    /// <summary>
    /// The infinite view: one bar covering the recording, first segment to last.
    ///
    /// The span is the whole recording and it grows by itself — a new segment moves the end, and
    /// because the view is fitted to that span through <see cref="ViewSpan"/> the timeline
    /// stretches to take it in without the operator touching anything. While a capture is running
    /// that means the bar is always the whole of what has been recorded so far.
    ///
    /// The on-air point is shown <i>within</i> it rather than ending it: solid up to the
    /// transmitter, a dashed outline for what is recorded but still in hand. That gap is the
    /// delay, and its size is the thing worth seeing at this zoom. An earlier version ended the
    /// bar at the transmitter, which answered "how much has gone out" at the cost of hiding how
    /// much there was.
    /// </summary>
    private void DrawOneBar(double width, long start, long span, double height)
    {
        List<Placed> of = Primary.OrderBy(p => p.StartFrame).ToList();

        if (of.Count == 0) return;

        long from = of[0].StartFrame;

        // The end of the last segment, not the sum of their lengths. Those differ the moment
        // there is a gap - a capture stopped and restarted - and the sum would draw a bar that
        // ended before the media did.
        long recorded = of.Max(p => p.EndFrame);

        long? onAir = OnAirFrame();

        // Clamped into the recording: the transmitter can be before a capture that was restarted
        // under it, and a bar drawn backwards is not a thing.
        long to = onAir is { } air ? Math.Clamp(air, from, recorded) : recorded;

        double a = Math.Max(0, (from - start) / (double)span * width);
        double b = Math.Min(width, (to - start) / (double)span * width);

        bool recording = Primary.Any(p => p.Pending);

        // What has gone out, drawn solid.
        TrackCanvas.Children.Add(new Rectangle
        {
            Width = Math.Max(2, b - a),
            Height = height,
            RadiusX = 3,
            RadiusY = 3,
            Fill = new SolidColorBrush(Color.FromRgb(0x23, 0x36, 0x4E)),
            Stroke = Brush(recording ? "Warn" : "Accent"),
            StrokeThickness = 1,
            Margin = new Thickness(a, 0, 0, 0),
        });

        // And what is recorded but not yet transmitted - the delay - as an outline past it. This
        // is the gap tidal lock is holding, and seeing its size is the point of the view.
        if (onAir is not null && recorded > to)
        {
            double c = Math.Min(width, (recorded - start) / (double)span * width);

            if (c > b)
                TrackCanvas.Children.Add(new Rectangle
                {
                    Width = c - b,
                    Height = height,
                    RadiusX = 3,
                    RadiusY = 3,
                    Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x3B, 0x82, 0xF6)),
                    Stroke = Brush("Muted"),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 3 },
                    Margin = new Thickness(b, 0, 0, 0),
                });
        }

        double end = Math.Min(width, (recorded - start) / (double)span * width);

        var label = new TextBlock
        {
            Text = onAir is null
                ? $"{of.Count} segment(s)   {RecordingFrames:N0} frames   " +
                  $"{Stamp(from)} - {Stamp(recorded)}   (nothing on air)"
                : $"{of.Count} segment(s)   on air {to - from:N0} of {RecordingFrames:N0} frames   " +
                  $"({recorded - to:N0} in hand)" + (recording ? "   (recording)" : ""),

            Foreground = Brush(recording ? "Warn" : "Text"),
            FontSize = 10.5,
            FontFamily = new FontFamily("Consolas"),
        };

        Canvas.SetLeft(label, a + 6);
        Canvas.SetTop(label, 8);
        TrackCanvas.Children.Add(label);

        // The two files at the ends, named. At this zoom the bar is the whole recording and the
        // only two segments an operator can point at are where it begins and where it leaves off,
        // so those are the two worth naming - the thirty in between have no room and no claim.
        End(of[0], a, left: true);

        if (of.Count > 1) End(of[^1], end, left: false);

        void End(Placed segment, double x, bool left)
        {
            var name = new TextBlock
            {
                Text = segment.Pending ? $"{segment.Clip.DisplayName}  (recording)"
                                       : segment.Clip.DisplayName,
                Foreground = Brush(segment.Pending ? "Warn" : "Muted"),
                FontSize = 9.5,
                FontFamily = new FontFamily("Consolas"),
                MaxWidth = Math.Max(60, width / 2 - 20),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = $"{segment.Clip.Name}\n{Stamp(segment.StartFrame)} - {Stamp(segment.EndFrame)}",
            };

            name.Measure(new Size(width, double.PositiveInfinity));

            Canvas.SetLeft(name, left
                ? x + 6
                : Math.Max(0, x - Math.Min(name.DesiredSize.Width, width / 2) - 6));

            Canvas.SetTop(name, Math.Max(26, height - 18));
            TrackCanvas.Children.Add(name);
        }
    }

    // ------------------------------------------------------------------ layers

    /// <summary>
    /// One file laid over the recording: an ad, a bed, a voice track.
    ///
    /// Held in frames of the day like everything else, so where it sits is comparable with the
    /// recording under it and with what is on air. <see cref="GainDb"/> is this item's own level,
    /// on top of whatever its lane is set to.
    /// </summary>
    private sealed class Overlay
    {
        public required string Path { get; init; }
        public required int Lane { get; init; }
        public required long StartFrame { get; set; }
        public required long Frames { get; init; }

        public double GainDb { get; set; }

        /// <summary>
        /// How this item sits against the recording, when it came from the Ingest list.
        ///
        /// Null for a file dragged in from Explorer onto one of the two plain layers, which is
        /// the older behaviour and still just a block at a time. An ingest item carries its
        /// plan, because the four modes mean different things about what goes to air and the
        /// block has to say which was chosen.
        /// </summary>
        public IngestPlan? Plan { get; init; }

        public string Name => System.IO.Path.GetFileName(Path);
        public long EndFrame => StartFrame + Frames;

        public bool Holds(long dayFrame) => dayFrame >= StartFrame && dayFrame < EndFrame;

        /// <summary>Where in the file a day frame falls.</summary>
        public long OffsetOf(long dayFrame) => Math.Clamp(dayFrame - StartFrame, 0, Math.Max(0, Frames - 1));
    }

    /// <summary>
    /// Two lanes a file can be dropped onto by hand. More would make the timeline a thicket at
    /// this height.
    /// </summary>
    private const int Lanes = 2;
    private const double LaneHeight = 36;

    /// <summary>
    /// The lane everything from the Ingest list goes on, below the two hand-drop lanes.
    ///
    /// One lane for all of it rather than one per item: these are the pieces being laid into
    /// this recording, and seeing them together in time order is the point. It is not a drop
    /// target for files from Explorer - an ingest placement carries a mode, and a file dragged
    /// in from outside has not been through the dialog that chooses one.
    /// </summary>
    private const int IngestLane = Lanes;

    private const int AllLanes = Lanes + 1;

    private readonly List<Overlay> _overlays = new();
    private Overlay? _selected;

    /// <summary>Per-lane level, applied on top of each item's own.</summary>
    private readonly double[] _laneGainDb = new double[AllLanes];

    /// <summary>
    /// The player for a lane, or null for the ingest lane.
    ///
    /// There are two layer players in the window and three lanes. The ingest lane is a plan
    /// rather than a monitor feed — what it will do on air is decided by the mode, and this
    /// window does not yet play that back — so it has no player and says so by returning null
    /// rather than quietly sharing one and playing the wrong thing.
    /// </summary>
    private MediaElement? LanePlayer(int lane) =>
        lane == 0 ? LayerA : lane == 1 ? LayerB : null;

    /// <summary>What is playing on each lane right now, so it is not restarted every frame.</summary>
    private readonly Overlay?[] _onLane = new Overlay?[AllLanes];

    // ------------------------------------------------------------------ dropping a file

    private void Overlay_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = Dropped(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// The data format an ingest item is dragged in.
    ///
    /// Its own format rather than FileDrop, so a drop can be told apart from a file dragged in
    /// from Explorer. The two are handled differently: an ingest item goes through the dialog
    /// that chooses how it sits against the recording, and an outside file is laid on a plain
    /// layer as before.
    /// </summary>
    private const string IngestDragFormat = "EmeraldIngestMedia";

    /// <summary>Where the pointer went down in the ingest list, to tell a click from a drag.</summary>
    private Point _ingestDragFrom;

    private void IngestList_MouseDown(object sender, MouseButtonEventArgs e) =>
        _ingestDragFrom = e.GetPosition(IngestList);

    /// <summary>
    /// Starts a drag once the pointer has actually moved.
    ///
    /// Judged against the system's own drag threshold, so a click that wobbles a pixel still
    /// selects and previews rather than starting a drag nobody asked for.
    /// </summary>
    private void IngestList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (IngestList.SelectedItem is not CapturedClip clip) return;

        Point now = e.GetPosition(IngestList);

        if (Math.Abs(now.X - _ingestDragFrom.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _ingestDragFrom.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var data = new DataObject();
        data.SetData(IngestDragFormat, clip.Path);

        DragDrop.DoDragDrop(IngestList, data, DragDropEffects.Copy);
    }

    /// <summary>The first file of a drop that Emerald can actually play, or null.</summary>
    private static string? Dropped(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(IngestDragFormat))
            return e.Data.GetData(IngestDragFormat) as string;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;

        return (e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>())
            .FirstOrDefault(MediaScanner.IsPlayable);
    }

    private static bool IsIngestDrag(DragEventArgs e) => e.Data.GetDataPresent(IngestDragFormat);

    /// <summary>
    /// Lays a dropped file on the lane and at the time it was dropped.
    ///
    /// Its length is probed rather than assumed, because the block's width on the timeline is the
    /// only thing that says how much of the recording it covers — a block drawn at the wrong
    /// length is a worse lie than no block at all.
    /// </summary>
    private void Overlay_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (Dropped(e) is not { } path) return;

        Point at = e.GetPosition(OverlayCanvas);

        int lane = Math.Clamp((int)(at.Y / LaneHeight), 0, Lanes - 1);

        long start = TimelineZoom.FrameAt(at.X, ViewStart, ViewSpan, OverlayCanvas.ActualWidth,
                                          _dayFrames - 1);

        long frames = 0;

        if (MediaProbe.LocateFfprobe(Ffmpeg.Locate(_settings.FfmpegPath)) is { } ffprobe &&
            MediaProbe.Probe(ffprobe, path, _rate) is { } info && info.Duration > TimeSpan.Zero)
            frames = (long)Math.Round(info.Duration.TotalSeconds * _rate);

        if (frames <= 0)
        {
            LayerText.Text = $"could not read the length of {System.IO.Path.GetFileName(path)}";
            LayerText.Foreground = Brush("Bad");

            ActivityLog.Shared.Warn(LogSource.App,
                $"Live Edit could not read the length of {System.IO.Path.GetFileName(path)}, " +
                "so it was not laid on a layer.",
                @event: "liveedit.dropfailed", file: path);
            return;
        }

        // From the Ingest list: ask how it should sit against the recording before placing it.
        //
        // The four modes are not interchangeable - three cover the recording and one lengthens
        // it, and only one changes the speed - so there is no sensible default to assume on the
        // operator's behalf. Cancelling places nothing.
        if (IsIngestDrag(e))
        {
            var ask = new IngestDropDialog(path, frames, start, _markIn, _markOut, _rate)
            {
                Owner = this,
            };

            if (ask.ShowDialog() != true) return;

            IngestPlan plan = ask.Plan;

            var placed = new Overlay
            {
                Path = path,
                Lane = IngestLane,
                StartFrame = plan.StartFrame,
                Frames = plan.Frames,
                Plan = plan,
            };

            _overlays.Add(placed);
            _selected = placed;

            ActivityLog.Shared.Info(LogSource.App,
                $"INGEST {placed.Name} - {plan.Mode} at {Stamp(plan.StartFrame)}, " +
                IngestPlacement.Describe(plan, _rate) + ".",
                @event: "liveedit.ingestplaced", file: path);

            Draw();
            ShowLayer();
            return;
        }

        var overlay = new Overlay { Path = path, Lane = lane, StartFrame = start, Frames = frames };

        _overlays.Add(overlay);
        _selected = overlay;

        ActivityLog.Shared.Info(LogSource.App,
            $"LAYER {overlay.Name} - on layer {(char)('A' + lane)} at {Stamp(start)}, " +
            $"{frames:N0} frames ({new Timecode(frames, _rate)}).",
            @event: "liveedit.layer", file: path,
            detail: $"layer {(char)('A' + lane)}\nat {Stamp(start)}\nframes {frames}");

        Draw();
        ShowLayer();
    }

    private void Overlay_Click(object sender, MouseButtonEventArgs e)
    {
        Point at = e.GetPosition(OverlayCanvas);

        // Every lane including the ingest one, so an ingest placement can be selected and
        // removed. A drop still only reaches the two hand-drop lanes - a file from Explorer has
        // not been through the dialog that chooses a mode - but selecting is not dropping.
        int lane = Math.Clamp((int)(at.Y / LaneHeight), 0, AllLanes - 1);

        long frame = TimelineZoom.FrameAt(at.X, ViewStart, ViewSpan, OverlayCanvas.ActualWidth,
                                          _dayFrames - 1);

        _selected = _overlays.FirstOrDefault(o => o.Lane == lane && o.Holds(frame));

        Draw();
        ShowLayer();
        Focus();
    }

    // ------------------------------------------------------------------ levels

    /// <summary>
    /// The level an item actually plays at: its own, plus its lane's.
    ///
    /// Added in decibels, which is multiplication of the amplitudes — the arithmetic an operator
    /// expects when they trim a lane and then trim one item on it.
    /// </summary>
    private double EffectiveGainDb(Overlay overlay) =>
        Math.Clamp(overlay.GainDb + _laneGainDb[overlay.Lane], -60, 12);

    private void NudgeSelectedGain(double by)
    {
        if (_selected is not { } overlay) return;

        overlay.GainDb = Math.Clamp(overlay.GainDb + by, -60, 12);

        ApplyLaneLevels();
        Draw();
        ShowLayer();

        ActivityLog.Shared.Info(LogSource.App,
            $"LAYER {overlay.Name} - set to {overlay.GainDb:+0.0;-0.0;0.0} dB " +
            $"({EffectiveGainDb(overlay):+0.0;-0.0;0.0} dB with layer " +
            $"{(char)('A' + overlay.Lane)} at {_laneGainDb[overlay.Lane]:+0.0;-0.0;0.0} dB).",
            @event: "liveedit.layerlevel", file: overlay.Path);
    }

    private void NudgeLaneGain(int lane, double by)
    {
        if (lane < 0 || lane >= Lanes) return;

        _laneGainDb[lane] = Math.Clamp(_laneGainDb[lane] + by, -60, 12);

        ApplyLaneLevels();
        Draw();
        ShowLayer();
    }

    private void DeleteSelected()
    {
        if (_selected is not { } overlay) return;

        _overlays.Remove(overlay);
        _selected = null;

        // It may be the one currently sounding.
        for (int lane = 0; lane < Lanes; lane++)
            if (ReferenceEquals(_onLane[lane], overlay))
            {
                LanePlayer(lane)?.Stop();
                _onLane[lane] = null;
            }

        ActivityLog.Shared.Info(LogSource.App,
            $"LAYER {overlay.Name} - taken off layer {(char)('A' + overlay.Lane)}.",
            @event: "liveedit.layerremoved", file: overlay.Path);

        Draw();
        ShowLayer();
    }

    /// <summary>Puts the current levels on the lane players, including the window's own mute.</summary>
    private void ApplyLaneLevels()
    {
        bool muted = MuteButton.IsChecked == true;

        for (int lane = 0; lane < Lanes; lane++)
        {
            if (LanePlayer(lane) is not { } player) continue;

            player.IsMuted = muted;

            player.Volume = _onLane[lane] is { } playing
                ? Math.Clamp(Math.Pow(10, EffectiveGainDb(playing) / 20) * LevelSlider.Value, 0, 1)
                : 0;
        }
    }

    private void ShowLayer()
    {
        if (_selected is not { } overlay)
        {
            // Nothing said while the layers are empty. The prompt that used to sit here was
            // permanent furniture in the transport bar explaining a feature to someone who had
            // not asked about it; the layers are still there and still take a dropped file.
            LayerText.Text = _overlays.Count == 0
                ? ""
                : $"{_overlays.Count} on layers - click one to adjust it";

            LayerText.Foreground = Brush("Muted");
            return;
        }

        // An ingest placement has no level of its own to talk about - what matters about it is
        // the mode and what that does, which is the sentence the plan already knows how to say.
        if (overlay.Plan is { } plan)
        {
            LayerText.Text = $"{overlay.Name}  {plan.Mode}: " +
                             IngestPlacement.Describe(plan, _rate) + "   Del removes";

            LayerText.Foreground = Brush("Accent");
            return;
        }

        LayerText.Text =
            $"{overlay.Name}  layer {(char)('A' + overlay.Lane)}  " +
            $"{overlay.GainDb:+0.0;-0.0;0.0} dB" +
            (_laneGainDb[overlay.Lane] != 0
                ? $" (+ layer {_laneGainDb[overlay.Lane]:+0.0;-0.0;0.0} = " +
                  $"{EffectiveGainDb(overlay):+0.0;-0.0;0.0})"
                : "") +
            "   [ ] level   Del remove";

        LayerText.Foreground = Brush("Ok");
    }

    // ------------------------------------------------------------------ playing the layers

    /// <summary>
    /// Starts and stops the lane players as the playhead crosses what is on them.
    ///
    /// Each lane is its own <see cref="MediaElement"/>, because one cannot mix: WPF plays a single
    /// source at a time. They are started against the playhead rather than against each other, so
    /// they line up to within whatever a seek costs — tens of milliseconds, which is fine for a
    /// bed or an ad under a picture and is not the frame-exact conform a finished render needs.
    /// </summary>
    private void FollowLayers()
    {
        for (int lane = 0; lane < Lanes; lane++)
        {
            Overlay? want = _playing
                ? _overlays.FirstOrDefault(o => o.Lane == lane && o.Holds(_at))
                : null;

            if (LanePlayer(lane) is not { } player) continue;

            // Already on it: left alone, so it is not reseeking every frame.
            if (ReferenceEquals(want, _onLane[lane])) continue;

            _onLane[lane] = want;

            if (want is null)
            {
                player.Stop();
                player.Source = null;
                continue;
            }

            player.Source = new Uri(want.Path);
            player.Position = TimeSpan.FromSeconds(want.OffsetOf(_at) / (double)_rate);
            player.Play();
        }

        ApplyLaneLevels();
    }

    private void StopLayers()
    {
        for (int lane = 0; lane < Lanes; lane++)
        {
            LanePlayer(lane)?.Stop();
            _onLane[lane] = null;
        }
    }

    // ------------------------------------------------------------------ transport

    private void StepBack_Click(object sender, RoutedEventArgs e) => Nudge(-1);
    private void StepForward_Click(object sender, RoutedEventArgs e) => Nudge(+1);

    /// <summary>
    /// One arrow-key press: one step of the current zoom — a minute at the hour view, a single
    /// frame at the bottom. Stepping by a frame across an hour-wide window would not move the
    /// playhead by a whole pixel.
    /// </summary>
    private void Nudge(int direction)
    {
        Pause();
        Seek(_at + direction * _ladder[_zoom].StepFrames);
    }

    // --------------------------------------------------------- picking a single frame

    /// <summary>
    /// One frame, whatever the zoom is. This is what the plain arrow keys do.
    ///
    /// A frame is the unit an edit is decided in, and it does not change because the view has
    /// been zoomed out — so neither does this. The zoom step is still reachable on shift for
    /// travelling, but it is no longer what an unmodified key means: at the hour view that made a
    /// press worth a minute, and there was no zoom level at which an operator could both see the
    /// shape of a recording and pick a frame out of it.
    ///
    /// The playhead can go off the edge of the window doing this, so the view is brought back to
    /// it the same way a seek would.
    /// </summary>
    private void NudgeFrame(long frames)
    {
        Pause();
        Seek(_at + frames);
    }

    /// <summary>
    /// Enter takes whatever is in the box and goes there; Escape puts the box back to where the
    /// playhead is and hands the keyboard back to the timeline.
    /// </summary>
    private void GoTo_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ShowGoTo(force: true);
            Keyboard.ClearFocus();
            Focus();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter) return;

        e.Handled = true;

        if (FramePick.TryParse(GoToBox.Text, _rate, _dayFrames, _at,
                               out long frame, out string? problem))
        {
            Pause();
            Seek(frame);

            // Not cleared: leaving the frame they just asked for in the box is how they see that
            // it was taken, and makes a second nudge from the same place one keystroke.
            ShowGoTo(force: true);
            GoToBox.SelectAll();
        }
        else
        {
            StoreText.Text = problem ?? "That is not a frame.";
        }
    }

    /// <summary>
    /// Leaving the box puts the playhead's own position back in it, so it never sits there
    /// showing a half-typed time that is not where anything is.
    /// </summary>
    private void GoTo_LostFocus(object sender, RoutedEventArgs e) => ShowGoTo();

    /// <summary>
    /// Writes the playhead into the go-to box.
    ///
    /// Called from the position readout, so the box tracks the playhead and is always a true
    /// statement of where things are rather than a blank field. It leaves the box alone while
    /// somebody is typing in it — overwriting a half-typed timecode twenty-five times a second
    /// during playback would make it impossible to use — so the two places that mean to replace
    /// what was typed say so.
    /// </summary>
    private void ShowGoTo(bool force = false)
    {
        if (!force && GoToBox.IsKeyboardFocusWithin) return;

        GoToBox.Text = Stamp(_at);
    }

    private void MarkIn_Click(object sender, RoutedEventArgs e) => Mark(inPoint: true);
    private void MarkOut_Click(object sender, RoutedEventArgs e) => Mark(inPoint: false);

    /// <summary>
    /// Sets a mark at the playhead, or takes it off if it is already here.
    ///
    /// <b>Setting it again where it already is means unset.</b> There was no way to undo a mark
    /// at all before: once in was down the range existed and the only way out was to put it
    /// somewhere harmless. Pressing the same mark twice without moving is the obvious gesture for
    /// "forget that", and it is unambiguous - the playhead has not moved, so nothing else could
    /// have been meant.
    ///
    /// Judged on the exact frame rather than near it, deliberately. A tolerance would mean a mark
    /// nudged one frame and re-pressed vanished instead of moving, and which of the two happened
    /// would depend on a number the operator cannot see.
    /// </summary>
    private void Mark(bool inPoint)
    {
        bool cleared;

        if (inPoint)
        {
            cleared = _markIn == _at;

            if (cleared) _markIn = null;
            else
            {
                _markIn = _at;
                if (_markOut is { } o && o <= _at) _markOut = null;
            }
        }
        else
        {
            cleared = _markOut == _at;

            if (cleared) _markOut = null;
            else
            {
                _markOut = _at;
                if (_markIn is { } i && i >= _at) _markIn = null;
            }
        }

        ActivityLog.Shared.Info(LogSource.App,
            cleared
                ? $"MARK  {(inPoint ? "in" : "out")} cleared at {Stamp(_at)} in Live Edit."
                : $"MARK  {(inPoint ? "in" : "out")} at {Stamp(_at)} in Live Edit" +
                  (_loaded is { } l ? $" ({l.Clip.Name})" : "") + ".",
            @event: "liveedit.mark", file: _loaded?.Clip.Path);

        Draw();
        ShowPosition();
    }

    // ------------------------------------------------------------------ zoom

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(+1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(-1);

    private void Zoom(int by)
    {
        int was = _zoom;
        _zoom = TimelineZoom.Step(_zoom, by, _rate);

        if (_zoom == was) return;

        ShowZoom();

        // Zooming is done to look at where the playhead is, so it stays in view and the thumb
        // is resized to match the new span.
        EnsureVisible(_at);
        ShowScroll();

        Draw();
        DrawOnAir();
    }

    private void ShowZoom()
    {
        ZoomLevel level = _ladder[_zoom];

        ZoomText.Text = level.Name;

        // What the keys step by is no longer written in the transport bar. The arrows are one
        // frame at every zoom now, so there was nothing left for a per-zoom readout to tell
        // anyone; the shift step is on the key hint at the top of the window.
        InfiniteButton.ToolTip = _infinite
            ? "Infinite view: the capture up to the on-air point. Click for every file on the clock."
            : $"Infinite view: the capture up to the on-air point. Shift+arrows step {level.StepName}.";
    }

    // ------------------------------------------------------------------ keys

    /// <summary>
    /// Space plays and pauses; the arrows seek by a step of the current zoom; + and - zoom;
    /// I and O mark; A jumps to what is on air.
    ///
    /// Handled as a preview so space does not reach whichever button has focus and press it
    /// instead. A key that is not ours is left alone, so the clip list still scrolls.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;

        switch (e.Key)
        {
            case Key.Space: PlayPause(); break;

            // Shift travels by a step of the current zoom - a minute at the hour view - for
            // getting across a recording rather than along it.
            case Key.Left when Keyboard.Modifiers == ModifierKeys.Shift: Nudge(-1); break;
            case Key.Right when Keyboard.Modifiers == ModifierKeys.Shift: Nudge(+1); break;

            // Ctrl is a second, the other size a cut gets looked for in.
            case Key.Left when Keyboard.Modifiers == ModifierKeys.Control: NudgeFrame(-_rate); break;
            case Key.Right when Keyboard.Modifiers == ModifierKeys.Control: NudgeFrame(+_rate); break;

            // Plain arrows are a frame, at every zoom. This is the unit an edit is decided in, so
            // it is what the bare key does; the zoom step moved to shift, where travelling
            // belongs. Before this, the arrows meant something different at each zoom level and
            // the only frame-exact key was a modifier nobody would guess.
            case Key.Left: NudgeFrame(-1); break;
            case Key.Right: NudgeFrame(+1); break;

            case Key.Home: Pause(); Seek(_placed.Count > 0 ? _placed[0].StartFrame : 0); break;
            case Key.End: Pause(); Seek(_placed.Count > 0 ? _placed[^1].EndFrame - 1 : 0); break;

            case Key.OemPlus or Key.Add: Zoom(+1); break;
            case Key.OemMinus or Key.Subtract: Zoom(-1); break;

            case Key.I: Mark(inPoint: true); break;
            case Key.O: Mark(inPoint: false); break;

            case Key.A: GoToOnAir(); break;

            case Key.B: EditBookmark(); break;

            case Key.M:
                MuteButton.IsChecked = MuteButton.IsChecked != true;
                ApplyLevel();
                break;

            // The selected layer item: a decibel at a time, and off.
            case Key.OemOpenBrackets: NudgeSelectedGain(-1); break;
            case Key.OemCloseBrackets: NudgeSelectedGain(+1); break;
            case Key.Delete or Key.Back: DeleteSelected(); break;

            // The lanes themselves, with shift held.
            case Key.D1 when Keyboard.Modifiers == ModifierKeys.Shift: NudgeLaneGain(0, -1); break;
            case Key.D2 when Keyboard.Modifiers == ModifierKeys.Shift: NudgeLaneGain(0, +1); break;
            case Key.D3 when Keyboard.Modifiers == ModifierKeys.Shift: NudgeLaneGain(1, -1); break;
            case Key.D4 when Keyboard.Modifiers == ModifierKeys.Shift: NudgeLaneGain(1, +1); break;

            default: return;
        }

        e.Handled = true;
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;

        Zoom(e.Delta > 0 ? +1 : -1);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ dragging the view

    /// <summary>Where the drag started: the mouse in pixels, and the view in frames.</summary>
    private Point _panFrom;
    private long _panFromScroll;
    private bool _panning;

    /// <summary>
    /// The canvas the drag started on, which is the one holding the mouse.
    ///
    /// Captured on that element rather than on the window, and this is the difference between
    /// working and not: capture routes every subsequent mouse event to the captured element, so
    /// capturing the window would have sent the move and the release somewhere the canvas
    /// handlers never see — a drag that began and then could not end.
    /// </summary>
    private UIElement? _panOn;

    /// <summary>
    /// Middle button down on the timeline grabs the view.
    ///
    /// The middle button, not the left: the left one puts the playhead somewhere, which is the
    /// thing an operator does most and must not become a drag. Pressing the wheel is the gesture
    /// every other timeline uses for this, and the hand cursor is how it says so.
    ///
    /// At the widest zoom the whole day is already on screen and there is nothing to drag, so it
    /// does not pretend otherwise - no cursor change, no capture.
    /// </summary>
    private void Pan_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (_infinite || Span >= _dayFrames) return;

        _panning = true;
        _panFrom = e.GetPosition(TrackCanvas);
        _panFromScroll = _scroll;

        Cursor = Cursors.Hand;

        // Captured so the drag survives the pointer leaving the canvas, which it will - dragging
        // a timeline to its edge is how you ask for more of it.
        _panOn = sender as UIElement;
        _panOn?.CaptureMouse();

        e.Handled = true;
    }

    private void Pan_Move(object sender, MouseEventArgs e)
    {
        if (!_panning) return;

        double width = TrackCanvas.ActualWidth;
        if (width <= 1) return;

        // Pixels dragged, converted to frames at this zoom. Dragging right shows earlier time,
        // which is the direction that feels like moving the film rather than the window.
        double moved = e.GetPosition(TrackCanvas).X - _panFrom.X;
        long by = (long)Math.Round(moved / width * ViewSpan);

        long want = _panFromScroll - by;

        if (want == _scroll) return;

        _scroll = want;
        ClampScroll();
        ShowScroll();
        Draw();
        DrawOnAir();
    }

    private void Pan_Up(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;

        EndPan();
        e.Handled = true;
    }

    /// <summary>
    /// The pointer leaving is not the end of a drag - the mouse is captured, so a drag past the
    /// edge is still a drag. This only matters for the case where the button was released
    /// somewhere this window never heard about, which would otherwise leave a hand cursor and a
    /// timeline that scrolls on mouse-move forever.
    /// </summary>
    private void Pan_Leave(object sender, MouseEventArgs e)
    {
        if (_panning && e.MiddleButton == MouseButtonState.Released) EndPan();
    }

    private void EndPan()
    {
        if (!_panning) return;

        _panning = false;
        Cursor = Cursors.Arrow;

        _panOn?.ReleaseMouseCapture();
        _panOn = null;
    }

    private void Timeline_Click(object sender, MouseButtonEventArgs e)
    {
        // A click on the timeline moves the playhead, and only that.
        //
        // Clicking a bookmark used to open it, which made the one thing an operator does most
        // here - put the playhead somewhere - conditional on what happened to be under the
        // pointer. At a day-wide zoom a bookmark is a few pixels wide and they are easy to hit
        // by accident, so a click meant to place the playhead opened a dialog instead. The
        // bookmarks are managed from the Bookmark button, where nothing else is competing for
        // the same gesture.
        Pause();

        Seek(TimelineZoom.FrameAt(e.GetPosition(TrackCanvas).X, ViewStart, ViewSpan,
                                  TrackCanvas.ActualWidth, _dayFrames - 1));

        Focus();
    }

    // ------------------------------------------------------------------ drawing

    private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ShowScroll();
        Draw();
        DrawOnAir();
    }

    /// <summary>A frame of the day, as station timecode.</summary>
    private string Stamp(long dayFrame) => new Timecode(dayFrame, _rate).ToString();

    /// <summary>
    /// A ruler interval that suits a span: the one belonging to the tightest rung that would
    /// still hold it. Falls back to the current rung's when nothing fits, which is the
    /// ordinary non-infinite case.
    /// </summary>
    private long TickFor(long span, ZoomLevel current)
    {
        if (!_infinite) return current.TickFrames;

        foreach (ZoomLevel rung in _ladder.OrderBy(r => r.SpanFrames))
            if (rung.SpanFrames >= span) return rung.TickFrames;

        return current.TickFrames;
    }

    private void ShowPosition()
    {
        PositionText.Text = Stamp(_at);

        PlayButton.IsEnabled = _loaded is not null;
        MarkInButton.IsEnabled = MarkOutButton.IsEnabled = true;

        // The marks read off the timeline, where they are drawn and labelled. What a readout here
        // can usefully add is the length of the range, which is on the Export button - the one
        // place it is about to matter - and in a tooltip rather than as text that grows sideways
        // across the volume slider.
        ExportButton.ToolTip = _markIn is { } a && _markOut is { } b && b > a
            ? $"Export {Stamp(a)} to {Stamp(b)}  ({new Timecode(b - a, _rate)}, {b - a:N0} frames)"
            : "Mark in and out on the timeline first";

        // There is nothing to export until both marks are set the right way round.
        ExportButton.IsEnabled = _markIn is { } mIn && _markOut is { } mOut && mOut > mIn;

        ShowGoTo();
    }

    /// <summary>
    /// The ruler, every segment of the day in its place, the marks and the playhead.
    ///
    /// What is drawn is bounded by the window rather than by the day, so an hour at one tick
    /// every five minutes is twelve labels and a handful of blocks whether the store holds an
    /// hour or a week.
    /// </summary>
    private void Draw()
    {
        RulerCanvas.Children.Clear();
        TrackCanvas.Children.Clear();

        double width = TrackCanvas.ActualWidth;
        if (width <= 1) return;

        ZoomLevel level = _ladder[_zoom];
        long span = ViewSpan;
        long start = ViewStart;

        var line = (Brush)FindResource("Line");
        var muted = (Brush)FindResource("Muted");

        // Ticks land on multiples of the interval rather than on the window's edge, so they stay
        // put as the window scrolls instead of sliding along with it.
        //
        // In the infinite view the span is the recording, not a rung, so the rung's interval is
        // the wrong size for it - an hour-long recording fitted to the screen with the 24-hour
        // rung's hourly ticks would carry one label. The rung that would have held this span is
        // borrowed instead, which keeps the intervals to the same round numbers as everywhere
        // else rather than inventing ragged ones.
        long tick = Math.Max(1, TickFor(span, level));

        for (long f = start / tick * tick; f <= start + span; f += tick)
        {
            if (f < 0 || f >= _dayFrames) continue;
            if (TimelineZoom.XOf(f, start, span, width) is not { } x) continue;

            RulerCanvas.Children.Add(new Line
            {
                X1 = x, X2 = x, Y1 = 14, Y2 = 24,
                Stroke = line, StrokeThickness = 1,
            });

            var label = new TextBlock { Text = Stamp(f), Foreground = muted, FontSize = 10 };

            Canvas.SetLeft(label, x + 3);
            Canvas.SetTop(label, 1);
            RulerCanvas.Children.Add(label);
        }

        double height = Math.Max(24, TrackCanvas.ActualHeight - 24);

        if (_infinite) DrawOneBar(width, start, span, height);

        // One row for every segment, whichever recording it came from.
        //
        // A row per recording folder was tried and taken out again: on a store with several
        // recordings in a day it cut the timeline into thin strips, each too short to carry a
        // name or a frame count, and the segments an operator was actually working on got a
        // third of the height. The overlap it was guarding against is real but rare, and it is
        // handled by drawing order instead - the recording in question goes on last, so it is
        // the one on top and the one that is clickable where two cover the same minute.
        IEnumerable<Placed> inOrder = _infinite
            ? Enumerable.Empty<Placed>()
            : _placed
                .OrderBy(p => MediaLibrary.RecordingFolder(p.Clip.Path) == _primaryFolder)
                .ThenBy(p => p.StartFrame);

        foreach (Placed placed in inOrder)
        {
            // Anything wholly outside the window costs nothing to skip and would cost a visual
            // for every segment of a day otherwise.
            if (placed.EndFrame < start || placed.StartFrame > start + span) continue;

            double from = Math.Max(0, (placed.StartFrame - start) / (double)span * width);
            double to = Math.Min(width, (placed.EndFrame - start) / (double)span * width);

            bool current = ReferenceEquals(placed, _loaded);

            // Whether this segment belongs to the recording in question. Where two recordings
            // cover the same minute the other one is dimmed, so the row reads as "this
            // recording, and something else also here" rather than as two equal claims.
            bool primary = _primaryFolder is null ||
                           MediaLibrary.RecordingFolder(placed.Clip.Path) == _primaryFolder;

            TrackCanvas.Children.Add(new Rectangle
            {
                Width = Math.Max(1, to - from),
                Height = height,
                RadiusX = 3,
                RadiusY = 3,

                // A segment still being recorded is drawn as an outline with no fill: it is on
                // the clock, it is where the recorder is, and it cannot be played yet.
                Fill = placed.Pending
                    ? new SolidColorBrush(Color.FromArgb(0x30, 0xF5, 0x9E, 0x0B))
                    : new SolidColorBrush(Color.FromRgb(0x23, 0x36, 0x4E)),

                Stroke = (Brush)FindResource(placed.Pending ? "Warn" : current ? "Ok" : "Accent"),
                StrokeThickness = current ? 2 : 1,
                Opacity = primary ? 1.0 : 0.5,
                Margin = new Thickness(from, 0, 0, 0),
            });

            // Only worth a name if there is room for one.
            if (to - from > 60)
            {
                var name = new TextBlock
                {
                    Text = placed.Pending ? $"{placed.Clip.DisplayName}  (recording)" : placed.Clip.DisplayName,
                    Foreground = Brush(placed.Pending ? "Warn" : primary ? "Text" : "Muted"),
                    FontSize = 10.5,
                    MaxWidth = Math.Max(20, to - from - 12),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };

                Canvas.SetLeft(name, from + 6);
                Canvas.SetTop(name, 7);
                TrackCanvas.Children.Add(name);

                // The frame count under the name, and the span it covers. Frames rather than
                // only a duration because this is the unit everything else here is in, and
                // "how many frames is that segment" is otherwise a sum nobody should do by hand.
                // An open segment says its count is the assumed one, not a measured one.
                var counts = new TextBlock
                {
                    Text = $"{placed.Frames:N0} frames  {Stamp(placed.StartFrame)} - {Stamp(placed.EndFrame)}" +
                           (placed.Pending ? "  (assumed)" : ""),
                    Foreground = Brush(placed.Pending ? "Warn" : "Muted"),
                    FontSize = 9.5,
                    FontFamily = new FontFamily("Consolas"),
                    MaxWidth = Math.Max(20, to - from - 12),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };

                Canvas.SetLeft(counts, from + 6);
                Canvas.SetTop(counts, 24);
                TrackCanvas.Children.Add(counts);
            }
        }

        // The range, when both ends are set.
        if (_markIn is { } inAt && _markOut is { } outAt && outAt > inAt)
        {
            double a = Math.Max(0, (inAt - start) / (double)span * width);
            double b = Math.Min(width, (outAt - start) / (double)span * width);

            if (b > a)
                TrackCanvas.Children.Add(new Rectangle
                {
                    Width = b - a,
                    Height = height,
                    Fill = new SolidColorBrush(Color.FromArgb(0x50, 0x4A, 0xD1, 0x95)),
                    Margin = new Thickness(a, 0, 0, 0),
                });
        }

        // And each end as a line of its own, whether the other end is set or not.
        //
        // A shaded range needs both marks before it shows anything, so marking in and then
        // looking for where it landed showed nothing at all. One mark is a decision worth seeing
        // on its own, and a mark outside the window is drawn nowhere rather than at the edge,
        // which would put it at a time it is not.
        DrawMark(_markIn, "IN");
        DrawMark(_markOut, "OUT");

        DrawBookmarks(width, start, span, height);

        void DrawMark(long? at, string label)
        {
            if (at is not { } frame) return;
            if (TimelineZoom.XOf(frame, start, span, width) is not { } x) return;

            TrackCanvas.Children.Add(new Rectangle
            {
                Width = 2,
                Height = height,
                Fill = Brush("Ok"),
                Margin = new Thickness(x, 0, 0, 0),
            });

            var tag = new TextBlock
            {
                Text = label,
                Foreground = Brush("Ok"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
            };

            // Inside the window: a label hard against the right edge would be clipped, so the
            // OUT tag sits to the left of its line when there is no room to its right.
            Canvas.SetLeft(tag, x + 4 + 24 > width ? Math.Max(0, x - 26) : x + 4);
            Canvas.SetTop(tag, height - 14);
            TrackCanvas.Children.Add(tag);
        }

        Playhead.Margin = new Thickness(TimelineZoom.XOf(_at, start, span, width) ?? 0, 0, 0, 0);

        DrawLayers(width, start, span);
    }

    /// <summary>
    /// The two layers: their dividing line, and whatever has been laid on them.
    ///
    /// Drawn from the same window as the recording above, so a block sits under the part of the
    /// recording it covers. The level is written on the block rather than hidden in a panel —
    /// an ad at the wrong level is the thing an operator is looking for, and a number they have
    /// to click to see is a number they will not check.
    /// </summary>
    private void DrawLayers(double width, long start, long span)
    {
        OverlayCanvas.Children.Clear();

        var line = (Brush)FindResource("Line");

        for (int lane = 0; lane < AllLanes; lane++)
        {
            double top = lane * LaneHeight;

            // The lane's own name and level, pinned at the left so they stay put as it scrolls.
            // The ingest lane is named rather than lettered: it is not a third drop target, it
            // is where what has been laid into this recording lives.
            var label = new TextBlock
            {
                Text = lane == IngestLane
                    ? "INGEST"
                    : _laneGainDb[lane] == 0
                        ? $"{(char)('A' + lane)}"
                        : $"{(char)('A' + lane)}  {_laneGainDb[lane]:+0.0;-0.0;0.0} dB",

                Foreground = Brush(lane == IngestLane ? "Accent"
                                   : _laneGainDb[lane] == 0 ? "Muted" : "Tc"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 9.5,
            };

            Canvas.SetLeft(label, 4);
            Canvas.SetTop(label, top + 2);
            OverlayCanvas.Children.Add(label);

            if (lane > 0)
            {
                OverlayCanvas.Children.Add(new Line
                {
                    X1 = 0, X2 = width, Y1 = top, Y2 = top,
                    Stroke = line, StrokeThickness = 1,
                });
            }
        }

        foreach (Overlay overlay in _overlays)
        {
            if (overlay.EndFrame < start || overlay.StartFrame > start + span) continue;

            double from = Math.Max(0, (overlay.StartFrame - start) / (double)span * width);
            double to = Math.Min(width, (overlay.EndFrame - start) / (double)span * width);

            bool chosen = ReferenceEquals(overlay, _selected);

            bool ingest = overlay.Plan is not null;

            var block = new Rectangle
            {
                Width = Math.Max(2, to - from),
                Height = LaneHeight - 6,
                RadiusX = 3,
                RadiusY = 3,

                // Ingest placements are blue against the layers' green, because they are a
                // different kind of thing: a layer is something laid over the monitor feed, and
                // an ingest placement is an instruction about what goes to air.
                Fill = new SolidColorBrush(ingest
                    ? Color.FromArgb(0x55, 0x3B, 0x82, 0xF6)
                    : Color.FromArgb(0x60, 0x22, 0xC5, 0x5E)),

                Stroke = Brush(chosen ? "Tc" : ingest ? "Accent" : "Ok"),
                StrokeThickness = chosen ? 2 : 1,

                // A stretched item is dashed: it is not playing at its recorded rate, and that
                // is worth seeing without reading the label.
                StrokeDashArray = overlay.Plan is { Stretched: true }
                    ? new DoubleCollection { 3, 2 }
                    : null,

                Margin = new Thickness(from, overlay.Lane * LaneHeight + 3, 0, 0),
                ToolTip = overlay.Plan is { } p
                    ? $"{overlay.Name}\n{p.Mode}: {IngestPlacement.Describe(p, _rate)}"
                    : overlay.Name,
            };

            OverlayCanvas.Children.Add(block);

            // What an ingest placement covers but does not itself fill - a replace in a range
            // longer than the media - drawn as an outline past the end of the block, because
            // that stretch is the original carrying on and the range is what was marked.
            if (overlay.Plan is { } plan && plan.Covers > plan.Frames)
            {
                double coversTo = Math.Min(width,
                    (plan.StartFrame + plan.Covers - start) / (double)span * width);

                if (coversTo > to)
                    OverlayCanvas.Children.Add(new Rectangle
                    {
                        Width = coversTo - to,
                        Height = LaneHeight - 6,
                        Fill = Brushes.Transparent,
                        Stroke = Brush("Muted"),
                        StrokeThickness = 1,
                        StrokeDashArray = new DoubleCollection { 2, 3 },
                        Margin = new Thickness(to, overlay.Lane * LaneHeight + 3, 0, 0),
                        ToolTip = "the rest of the marked range - the original plays on here",
                    });
            }

            if (to - from > 50)
            {
                var name = new TextBlock
                {
                    Text = overlay.Plan is { } said
                        ? $"{overlay.Name}   {said.Mode}" +
                          (said.Stretched ? $" {said.Speed:0.###}x" : "")
                        : $"{overlay.Name}   {EffectiveGainDb(overlay):+0.0;-0.0;0.0} dB",

                    Foreground = (Brush)FindResource("Text"),
                    FontSize = 9.5,
                    FontFamily = new FontFamily("Consolas"),
                    MaxWidth = Math.Max(20, to - from - 8),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };

                Canvas.SetLeft(name, from + 5);
                Canvas.SetTop(name, overlay.Lane * LaneHeight + 10);
                OverlayCanvas.Children.Add(name);
            }
        }
    }

    private void LiveEditWindow_Closing(object? sender, CancelEventArgs e)
    {
        _tick.Stop();
        _watch.Stop();

        // Written on the way out as well as on every change, so a note typed and then forgotten
        // about is not lost by closing the window.
        SaveBookmarks();

        StopMonitor();
        StopLayers();
        LayerA.Close();
        LayerB.Close();

        Player.Stop();
        Player.Close();
    }

    // ------------------------------------------------------------- the two media tabs

    /// <summary>True while the right-hand panel is showing exported ranges rather than captures.</summary>
    private bool _showingIngest;

    /// <summary>
    /// Switches the right-hand panel between what was recorded and what has been cut out of it.
    ///
    /// Two toggles rather than radio buttons because the rest of this window is toggles, but they
    /// behave as one choice: clicking the lit one puts it back rather than leaving neither on and
    /// the panel empty.
    /// </summary>
    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        bool ingest = ReferenceEquals(sender, IngestTab);

        CaptureTab.IsChecked = !ingest;
        IngestTab.IsChecked = ingest;

        if (ingest == _showingIngest) return;

        _showingIngest = ingest;

        ClipList.Visibility = ingest ? Visibility.Collapsed : Visibility.Visible;
        IngestPanel.Visibility = ingest ? Visibility.Visible : Visibility.Collapsed;

        if (ingest) LoadIngest(); else LoadClips();
    }

    /// <summary>
    /// Opens whichever folder the panel is showing, in Explorer.
    ///
    /// The capture store or the ingest folder beside it, depending on the tab - the two are not
    /// the same place and the one being looked at is the one meant.
    /// </summary>
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string store = MediaLibrary.FolderFor(_settings);
        string folder = _showingIngest ? RangeExport.FolderFor(store) : store;

        try
        {
            Directory.CreateDirectory(folder);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder)
            {
                UseShellExecute = true,
            });

            StoreText.Text = folder;
        }
        catch (Exception ex) when (ex is IOException or
                                   System.ComponentModel.Win32Exception or
                                   UnauthorizedAccessException)
        {
            // The path still said out loud, which is most of what was wanted.
            StoreText.Text = $"{folder}  ({ex.Message})";
        }
    }

    private bool _listingIngest;

    /// <summary>
    /// Reads the ingest folder.
    ///
    /// Off the dispatcher for the same reason the capture list is: it probes every file, and an
    /// operator who has exported a day's worth would otherwise watch the window freeze.
    /// </summary>
    private void LoadIngest()
    {
        if (_listingIngest) return;
        _listingIngest = true;

        string store = MediaLibrary.FolderFor(_settings);
        string folder = RangeExport.FolderFor(store);
        string? ffprobe = MediaProbe.LocateFfprobe(Ffmpeg.Locate(_settings.FfmpegPath));
        int rate = _rate;

        StoreText.Text = $"reading {folder}...";

        Task.Run(() => RangeExport.List(store, ffprobe, rate))
            .ContinueWith(t =>
            {
                _listingIngest = false;

                IReadOnlyList<CapturedClip> clips =
                    t.IsFaulted ? Array.Empty<CapturedClip>() : t.Result;

                _rebinding = true;
                IngestList.ItemsSource = clips;
                _rebinding = false;

                // Not while an export is running: "nothing exported yet" over the top of a
                // progress bar is two statements that cannot both be true.
                IngestEmptyText.Visibility =
                    clips.Count == 0 && !_exporting ? Visibility.Visible : Visibility.Collapsed;

                StoreText.Text = clips.Count == 0
                    ? $"Exports land in {folder}."
                    : $"{clips.Count} export{(clips.Count == 1 ? "" : "s")} in {folder}";

            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Selecting an export previews it in the monitor and says what was written about it.
    ///
    /// The monitor is the only place an export can be looked at. It has no place on the station
    /// day — it is a range cut out of one, not a thing that happened at a time — so there is no
    /// position on this ruler that would mean anything for it, and the big screen is the ruler's.
    /// </summary>
    private void IngestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebinding) return;
        if (IngestList.SelectedItem is not CapturedClip clip) return;

        // Played through the small H.264 copy written beside it. The export itself is DNxHD,
        // which Media Foundation cannot decode, so playing the export directly gave a blank
        // picture and no reason for it.
        string? preview = RangeExport.PreviewFor(clip.Path);

        if (preview is not null) ShowInMonitor(preview, clip.DisplayName, actual: clip.Path);
        else
        {
            StopMonitor();

            _monitoring = clip.Path;

            MonitorText.Text = clip.DisplayName;
            MonitorPlayButton.IsEnabled = MonitorStopButton.IsEnabled = false;
            MonitorDeleteButton.Visibility = Visibility.Collapsed;

            MonitorPlaceholder.Text =
                $"{clip.Name}\n\nno preview copy beside it. Exports are cut from the DNxHD " +
                "masters, which this application cannot decode. Open it in a player, or export " +
                "the range again - new exports get a preview.";

            MonitorPlaceholder.Visibility = Visibility.Visible;
        }

        string said = ExportNote(clip.Path);

        StoreText.Text = said.Length > 0
            ? $"{clip.Name}  -  {said}"
            : $"{clip.Name}  -  {clip.Path}";
    }

    /// <summary>What the sidecar says about an export, or nothing if it cannot be read.</summary>
    private static string ExportNote(string mediaPath)
    {
        try
        {
            string sidecar = System.IO.Path.ChangeExtension(mediaPath, ".json");

            if (!File.Exists(sidecar)) return "";

            using System.Text.Json.JsonDocument doc =
                System.Text.Json.JsonDocument.Parse(File.ReadAllText(sidecar));

            return doc.RootElement.TryGetProperty("metadata", out System.Text.Json.JsonElement m)
                ? m.GetString() ?? ""
                : "";
        }
        catch (IOException) { return ""; }
        catch (System.Text.Json.JsonException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    // ------------------------------------------------------------- exporting a range

    /// <summary>
    /// The files the marked range covers, and what the result should be called.
    ///
    /// <b>Masters where there are masters.</b> The viewer plays proxies because they scrub; an
    /// export is material somebody will transmit, so it comes off the DNxHD. But all or none: the
    /// parts are joined with a stream copy, and mixing a master and a proxy would hand ffmpeg two
    /// codecs and produce either a failure or something worse. A range where any segment is
    /// missing its master falls back to proxies throughout, and the log says which it used.
    ///
    /// A segment still being written is left out entirely. It has no readable duration, so it is
    /// laid out on an assumption, and cutting against an assumption is how you export the wrong
    /// material.
    /// </summary>
    private (IReadOnlyList<RangePart> Parts, string Extension, bool Masters) PlanRange(
        long inFrame, long outFrame)
    {
        List<Placed> touched = _placed
            .Where(p => !p.Pending && p.EndFrame > inFrame && p.StartFrame < outFrame)
            .OrderBy(p => p.StartFrame)
            .ToList();

        bool masters = touched.Count > 0 && touched.All(p => p.Clip.HasMaster);

        List<(string Path, long StartFrame, long Frames)> segments = touched
            .Select(p => (Path: masters ? p.Clip.MasterPath! : p.Clip.Path, p.StartFrame, p.Frames))
            .ToList();

        string source = segments.Count > 0 ? segments[0].Path : ".mp4";

        return (RangePlan.Parts(segments, inFrame, outFrame),
                System.IO.Path.GetExtension(source).TrimStart('.'),
                masters);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_markIn is not { } inFrame || _markOut is not { } outFrame || outFrame <= inFrame)
            return;

        (IReadOnlyList<RangePart> parts, string extension, bool masters) =
            PlanRange(inFrame, outFrame);

        if (parts.Count == 0)
        {
            StoreText.Text = "The marked range covers no finished media - a segment still being "
                           + "recorded cannot be exported until the recorder closes it.";
            return;
        }

        if (Ffmpeg.Locate(_settings.FfmpegPath) is not { } ffmpeg)
        {
            StoreText.Text = "ffmpeg was not found, so a range cannot be cut.";
            return;
        }

        string store = MediaLibrary.FolderFor(_settings);

        var dialog = new ExportDialog(
            RangePlan.SuggestName(RecordingName(), new Timecode(inFrame, _rate)),
            extension,
            new Timecode(inFrame, _rate), new Timecode(outFrame, _rate),
            _rate, parts, store)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true) return;

        string name = dialog.ExportName;
        string metadata = dialog.Metadata;
        int rate = _rate;

        ActivityLog.Shared.Info(LogSource.App,
            $"EXPORT  {name}.{extension} from {new Timecode(inFrame, rate)} to "
          + $"{new Timecode(outFrame, rate)}, {parts.Count} segment"
          + $"{(parts.Count == 1 ? "" : "s")}, from the {(masters ? "masters" : "proxies")}.",
            @event: "liveedit.export",
            detail: metadata.Length > 0 ? metadata : null);

        // The cut itself. Long on a long range, so it runs off the dispatcher, and the button goes
        // dead meanwhile rather than letting a second cut of the same name start behind the first.
        ExportButton.IsEnabled = false;

        // Shown on the Ingest tab, and switched to it now rather than when it finishes: an
        // operator who pressed Export should be looking at the thing being made, not at a
        // timeline that gives no sign anything is happening.
        ShowExporting($"{name}.{extension}", "starting");

        if (!_showingIngest) Tab_Changed(IngestTab, new RoutedEventArgs());

        Task.Run(() => RangeExport.Write(
                     ffmpeg, store, name, metadata, parts, rate, extension,
                     // Off the dispatcher, so it hops back before touching the banner.
                     progress: stage => Dispatcher.BeginInvoke(
                         () => ShowExporting($"{name}.{extension}", stage))))
            .ContinueWith(t =>
            {
                ExportButton.IsEnabled = true;
                HideExporting();

                ExportResult result = t.IsFaulted
                    ? new ExportResult(false, "", t.Exception?.GetBaseException().Message ?? "failed")
                    : t.Result;

                if (!result.Ok)
                {
                    StoreText.Text = $"Export failed: {result.Problem}";

                    ActivityLog.Shared.Warn(LogSource.App,
                        $"EXPORT failed: {result.Problem}", @event: "liveedit.exportfailed");

                    // The list still refreshed: a failure can leave a part-written file, and
                    // seeing it is better than being told about it.
                    if (_showingIngest) LoadIngest();
                    return;
                }

                ActivityLog.Shared.Ok(LogSource.App,
                    $"EXPORT wrote {System.IO.Path.GetFileName(result.Path)}.",
                    @event: "liveedit.exported", file: result.Path);

                // Show them the thing they just made, rather than leaving them on the timeline
                // wondering whether it worked.
                if (_showingIngest) LoadIngest();
                else Tab_Changed(IngestTab, new RoutedEventArgs());

                StoreText.Text = $"Exported {System.IO.Path.GetFileName(result.Path)}.";

            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>True while an export is being written, so the panel does not contradict itself.</summary>
    private bool _exporting;

    /// <summary>
    /// Puts the Ingest panel into its working state, and says which part is running.
    ///
    /// The stage matters because the two halves of an export are nothing like each other in
    /// length: the cut is a stream copy and finishes in seconds, and the playable copy beside it
    /// is a real encode that can run for minutes on a long range. One unchanging "writing..."
    /// across both is indistinguishable from a hang, which is the thing an operator reaches for
    /// Task Manager over.
    /// </summary>
    private void ShowExporting(string name, string stage)
    {
        _exporting = true;

        ExportBusyName.Text = name;
        ExportBusyStage.Text = stage;
        ExportBusy.Visibility = Visibility.Visible;

        IngestEmptyText.Visibility = Visibility.Collapsed;

        StoreText.Text = $"{name} - {stage}";
    }

    private void HideExporting()
    {
        _exporting = false;
        ExportBusy.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// What to call the recording a range came out of: its folder, which is named for the moment
    /// the recorder rolled.
    /// </summary>
    private string RecordingName()
    {
        string? folder = _placed.Count > 0
            ? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(_placed[0].Clip.Path))
            : null;

        return string.IsNullOrEmpty(folder) ? _day.ToString("yyyy-MM-dd") : folder;
    }

    // ------------------------------------------------------------------ bookmarks

    /// <summary>
    /// The day's edit and censor points. One book per day, read when the ruler moves to it.
    /// </summary>
    private BookmarkBook _bookmarks = new();

    private void LoadBookmarks()
    {
        _bookmarks = BookmarkBook.Load(BookmarkBook.PathFor(_day));

        if (_bookmarks.Count > 0)
            ActivityLog.Shared.Info(LogSource.App,
                $"Live Edit read {_bookmarks.Count} bookmark(s) for {_day:yyyy-MM-dd}.",
                @event: "liveedit.bookmarks");
    }

    private void SaveBookmarks()
    {
        if (!_bookmarks.Save(BookmarkBook.PathFor(_day)))
            StoreText.Text = "The bookmarks could not be written to disk.";
    }

    private void Bookmark_Click(object sender, RoutedEventArgs e) => EditBookmark();

    /// <summary>
    /// Puts a bookmark on the playhead, or opens the one already there.
    ///
    /// "Already there" is judged with the same tolerance the eye uses - within a few pixels of
    /// the current zoom - because the playhead is never on the exact frame a mark was set from
    /// across a day-wide ruler, and a strict match would mean pressing B beside a bookmark
    /// silently made a second one a pixel away from the first.
    ///
    /// That is the opposite of the choice made for the in/out marks, and deliberately: in and out
    /// are set from the playhead and re-pressed from the same place, so exactness there is what
    /// makes the toggle unambiguous. A bookmark is also something you go back to by eye.
    /// </summary>
    /// <summary>
    /// Opens the day's bookmarks: all of them, with the fields to add and amend.
    ///
    /// The window works on the book directly, so what comes back is only whether anything
    /// changed and whether the operator asked to be taken somewhere.
    /// </summary>
    private void EditBookmark()
    {
        int before = _bookmarks.Count;

        var dialog = new BookmarkDialog(_bookmarks, _rate, _dayFrames, _at, _day)
        {
            Owner = this,
        };

        dialog.ShowDialog();

        if (dialog.Changed)
        {
            SaveBookmarks();

            ActivityLog.Shared.Info(LogSource.App,
                $"BOOKMARKS changed in Live Edit - {_bookmarks.Count} now on " +
                $"{_day:yyyy-MM-dd} (was {before}).",
                @event: "liveedit.bookmark",
                detail: string.Join("\n", _bookmarks.All.Select(
                    m => $"{new Timecode(m.Frame, _rate)}  {m.Label}")));

            Draw();
        }

        if (dialog.GoTo is { } frame)
        {
            Pause();
            Seek(frame);
        }
    }

    /// <summary>
    /// How many frames one pixel of the timeline is worth, at least one.
    ///
    /// The conversion between what the operator can see and what the timeline counts in, which is
    /// what every "did they mean this one" question here reduces to.
    /// </summary>
    private long FramesPerPixel()
    {
        double width = TrackCanvas.ActualWidth;

        return width <= 1 ? 1 : Math.Max(1, (long)Math.Round(ViewSpan / width));
    }

    /// <summary>
    /// The bookmarks in the window: an amber line each, named where there is room to name them.
    ///
    /// Amber because the two colours already on this timeline mean something that must not be
    /// confused with an editorial note - green is the in/out range an export is cut from, red is
    /// where the transmitter actually is.
    ///
    /// The note is written beside the line once the zoom is close enough that lines are not on
    /// top of each other, and is on the tooltip at every zoom. Both, because at a day-wide view
    /// thirty censor points are thirty lines three pixels apart and any label would be a smear -
    /// but the operator still needs to be able to ask which one is which.
    /// </summary>
    private void DrawBookmarks(double width, long start, long span, double height)
    {
        // Labels once a pixel is worth less than a second, which is the "sufficiently zoomed in"
        // part. Zoom alone is not enough though: two censor points four seconds apart are four
        // pixels apart at that threshold, and their notes would print over each other into an
        // unreadable smear. So a label is also only drawn where the last one has finished, and
        // the rest are reachable on hover. Zooming in further spreads them out and they appear.
        bool named = span / Math.Max(1.0, width) < _rate;

        double lastLabelRight = double.NegativeInfinity;

        var amber = Brush("Warn");

        foreach (Bookmark mark in _bookmarks.Between(start, start + span))
        {
            if (TimelineZoom.XOf(mark.Frame, start, span, width) is not { } x) continue;

            string tip = $"{Stamp(mark.Frame)}  -  {mark.Label}";

            TrackCanvas.Children.Add(new Rectangle
            {
                Width = 2,
                Height = height,
                Fill = amber,
                Margin = new Thickness(x, 0, 0, 0),
                ToolTip = tip,
            });

            // A two-pixel line is almost impossible to hover deliberately, so an invisible
            // wider strip over it carries the tooltip. Transparent rather than null fill - a
            // null-filled shape is not hit-testable at all, which is the usual way this is got
            // wrong and would leave the tooltip unreachable.
            TrackCanvas.Children.Add(new Rectangle
            {
                Width = 11,
                Height = height,
                Fill = Brushes.Transparent,
                Margin = new Thickness(Math.Max(0, x - 4.5), 0, 0, 0),
                ToolTip = tip,
            });

            if (!named || x < lastLabelRight) continue;

            // Measured so the next one knows where this ends. A fixed width would either waste
            // room on "cut" or clip "the guest named a minor".
            var label = new TextBlock
            {
                Text = mark.Label,
                Foreground = amber,
                FontSize = 9.5,
                FontFamily = new FontFamily("Consolas"),
                MaxWidth = 170,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = tip,
            };

            label.Measure(new Size(170, double.PositiveInfinity));

            double text = Math.Min(170, label.DesiredSize.Width);

            // Above the frame counts on the clip blocks, and flipped to the left of its line
            // near the right edge so it is not clipped off the canvas.
            bool flip = x + 5 + text > width;

            Canvas.SetLeft(label, flip ? Math.Max(0, x - text - 4) : x + 5);
            Canvas.SetTop(label, 42);
            TrackCanvas.Children.Add(label);

            lastLabelRight = (flip ? x : x + 5 + text) + 6;
        }
    }

    // ------------------------------------------------- the monitor for the selected file

    /// <summary>What the small monitor is showing, so a failure can name it and offer to bin it.</summary>
    private string? _monitoring;

    private bool _monitorPlaying;

    /// <summary>
    /// Shows a file in the small monitor beside the lists.
    ///
    /// Separate from <see cref="Player"/> on purpose. The big screen follows the timeline, and
    /// loading a list selection into it used to move the playhead - so checking whether a file
    /// was the right one cost the operator the position they had lined up. Nothing here touches
    /// the day, the playhead or the marks.
    /// </summary>
    /// <param name="actual">
    /// The file this preview stands for, when they differ — an export is played through the small
    /// copy beside it, but Delete and the log must name the export itself and not its stand-in.
    /// </param>
    private void ShowInMonitor(string path, string caption, string? actual = null)
    {
        StopMonitor();

        _monitoring = actual ?? path;

        MonitorText.Text = caption;
        MonitorDeleteButton.Visibility = Visibility.Collapsed;
        MonitorPlaceholder.Text = "opening...";
        MonitorPlaceholder.Visibility = Visibility.Visible;

        MonitorPlayButton.IsEnabled = MonitorStopButton.IsEnabled = false;

        try
        {
            Monitor.Source = new Uri(path);

            // Paused on the first frame rather than playing: a list selection is a question
            // about what a file is, and audio starting unbidden over a studio monitor is its
            // own kind of problem.
            Monitor.Play();
            Monitor.Pause();
        }
        catch (UriFormatException)
        {
            MonitorFailed($"{System.IO.Path.GetFileName(path)} is not a path this can open.");
        }
    }

    private void StopMonitor()
    {
        _monitorPlaying = false;
        _monitorTick.Stop();
        _monitorDragging = false;

        try
        {
            Monitor.Stop();
            Monitor.Close();
        }
        catch (InvalidOperationException) { }

        Monitor.Source = null;
        MonitorPlayButton.Content = "▶";

        _monitorLength = 0;
        MonitorSeek.IsEnabled = false;
        SetMonitorSeek(0);
        ShowMonitorTime();
    }

    private void Monitor_Opened(object sender, RoutedEventArgs e)
    {
        MonitorPlaceholder.Visibility = Visibility.Collapsed;
        MonitorDeleteButton.Visibility = Visibility.Collapsed;
        MonitorPlayButton.IsEnabled = MonitorStopButton.IsEnabled = true;

        // The seek bar can only be set up once the length is known, which is here. A file whose
        // duration cannot be read - a growing segment, an odd container - leaves the bar
        // disabled rather than showing a scale that means nothing.
        double seconds = Monitor.NaturalDuration.HasTimeSpan
            ? Monitor.NaturalDuration.TimeSpan.TotalSeconds
            : 0;

        _monitorLength = seconds;

        MonitorSeek.IsEnabled = seconds > 0;
        MonitorSeek.Maximum = seconds > 0 ? seconds : 1;

        SetMonitorSeek(Monitor.Position.TotalSeconds);
        ShowMonitorTime();

        _monitorTick.Start();
    }

    /// <summary>
    /// Moves the seek bar while the preview plays.
    ///
    /// Four times a second, not every frame: this is a position readout on a thumbnail, and the
    /// dispatcher has a 25 ms timeline loop and a live preview to serve. Nothing here is
    /// measured against it.
    /// </summary>
    private readonly DispatcherTimer _monitorTick =
        new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };

    private double _monitorLength;

    /// <summary>True while the operator has hold of the thumb, or while we are moving it.</summary>
    private bool _monitorDragging;
    private bool _settingMonitorSeek;

    private void OnMonitorTick()
    {
        if (Monitor.Source is null) { _monitorTick.Stop(); return; }

        // Not while it is being dragged: writing the player's position into the thumb the
        // operator is holding fights them for it.
        if (_monitorDragging) return;

        SetMonitorSeek(Monitor.Position.TotalSeconds);
        ShowMonitorTime();
    }

    /// <summary>
    /// Writes a position into the bar without that being read back as a seek request.
    ///
    /// ValueChanged fires whoever moved it, so without this flag every tick would look like the
    /// operator asking to seek and the player would be re-seeked to where it already was, four
    /// times a second.
    /// </summary>
    private void SetMonitorSeek(double seconds)
    {
        _settingMonitorSeek = true;
        MonitorSeek.Value = Math.Clamp(seconds, 0, MonitorSeek.Maximum);
        _settingMonitorSeek = false;
    }

    private void MonitorSeek_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settingMonitorSeek || Monitor.Source is null) return;

        // Applied as it moves, so dragging scrubs rather than jumping only on release -
        // ScrubbingEnabled is on, so the picture follows the thumb.
        Monitor.Position = TimeSpan.FromSeconds(e.NewValue);

        ShowMonitorTime();
    }

    private void MonitorSeek_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) =>
        _monitorDragging = true;

    private void MonitorSeek_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _monitorDragging = false;

        if (Monitor.Source is not null) Monitor.Position = TimeSpan.FromSeconds(MonitorSeek.Value);
    }

    private void ShowMonitorTime()
    {
        if (Monitor.Source is null || _monitorLength <= 0)
        {
            MonitorTimeText.Text = "--:-- / --:--";
            return;
        }

        MonitorTimeText.Text = $"{Clock(Monitor.Position.TotalSeconds)} / {Clock(_monitorLength)}";

        static string Clock(double seconds) =>
            TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss");
    }

    private void Monitor_Ended(object sender, RoutedEventArgs e)
    {
        _monitorPlaying = false;
        MonitorPlayButton.Content = "▶";

        Monitor.Position = TimeSpan.Zero;
        Monitor.Pause();
    }

    /// <summary>
    /// A file that will not open. This is the one case on this panel worth an action, because the
    /// usual reason is a recording that was cut off - an ffmpeg killed, a disk filled, a machine
    /// restarted mid-segment - which leaves a file with no index that will never play.
    /// </summary>
    private void Monitor_Failed(object sender, ExceptionRoutedEventArgs e) =>
        MonitorFailed(e.ErrorException?.Message ?? "the file could not be opened");

    private void MonitorFailed(string why)
    {
        _monitorPlaying = false;
        MonitorPlayButton.IsEnabled = MonitorStopButton.IsEnabled = false;

        MonitorPlaceholder.Text = _monitoring is { } path
            ? $"{System.IO.Path.GetFileName(path)} will not play.\n\n{why}"
            : why;

        MonitorPlaceholder.Visibility = Visibility.Visible;

        // Offered only for a file that actually failed. A delete button standing beside every
        // clip in a running plant's store is an accident waiting to be clicked.
        MonitorDeleteButton.Visibility = _monitoring is not null ? Visibility.Visible : Visibility.Collapsed;

        ActivityLog.Shared.Warn(LogSource.App,
            $"Live Edit could not open {System.IO.Path.GetFileName(_monitoring ?? "a file")}: {why}",
            @event: "liveedit.unreadable", file: _monitoring);
    }

    private void MonitorPlay_Click(object sender, RoutedEventArgs e)
    {
        if (Monitor.Source is null) return;

        if (_monitorPlaying)
        {
            Monitor.Pause();
            _monitorPlaying = false;
            MonitorPlayButton.Content = "▶";
        }
        else
        {
            Monitor.Play();
            _monitorPlaying = true;
            MonitorPlayButton.Content = "II";
        }
    }

    private void MonitorStop_Click(object sender, RoutedEventArgs e)
    {
        if (Monitor.Source is null) return;

        Monitor.Pause();
        Monitor.Position = TimeSpan.Zero;

        _monitorPlaying = false;
        MonitorPlayButton.Content = "▶";
    }

    /// <summary>
    /// Deletes a file that will not open, once the operator has said so twice over.
    ///
    /// <b>Guarded harder than it looks.</b> This deletes real media off a plant's disk, and the
    /// window it is offered from sits beside a running recording. So: it is only ever offered for
    /// a file that actually failed to open, it refuses a segment the recorder still has open -
    /// which is exactly what the newest file looks like, unreadable because its index is not
    /// written yet - and it names the file and its size in the question rather than asking
    /// "are you sure".
    ///
    /// The master beside it goes too when there is one. A proxy deleted on its own would leave a
    /// master with nothing to find it by, which is worse than either outcome.
    /// </summary>
    private void MonitorDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_monitoring is not { } path) return;

        // The one that is still being written is unreadable for a good reason and will play
        // perfectly once the recorder closes it. Deleting it would destroy live media.
        if (Pending(path))
        {
            MessageBox.Show(this,
                $"{System.IO.Path.GetFileName(path)} is still being recorded.\n\n" +
                "It cannot be read yet because ffmpeg writes the index last, and it will play " +
                "normally once the segment closes. Nothing has been deleted.",
                "Still recording", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Everything that belongs to this one piece of media.
        //
        // A file in the store is a proxy with a master beside it. An export is a .mov with an
        // H.264 preview copy and a .json of its metadata beside it. Deleting only the one that
        // failed to open would leave the rest behind - the Ingest list reads the folder, so a
        // sidecar and a preview with no media would keep showing up as a broken row that could
        // not be cleared, and the operator would have to go to Explorer to finish the job.
        List<string> victims = Companions(path);

        long bytes = victims.Sum(Size);

        string question =
            "Delete this from the disk?\n\n" +
            string.Join("\n", victims) +
            $"\n\n{bytes / (double)(1L << 20):F0} MB in total, " +
            $"{victims.Count} file{(victims.Count == 1 ? "" : "s")}.\n\n" +
            "This cannot be undone.";

        if (MessageBox.Show(this, question, "Delete unreadable file",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning,
                            MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        StopMonitor();

        var gone = new List<string>();
        string? failed = null;

        foreach (string victim in victims)
        {
            try
            {
                if (File.Exists(victim)) File.Delete(victim);
                gone.Add(victim);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed = ex.Message;
                break;
            }
        }

        if (failed is not null)
        {
            StoreText.Text = $"Could not delete: {failed}";

            ActivityLog.Shared.Warn(LogSource.App,
                $"Live Edit failed to delete {System.IO.Path.GetFileName(path)}: {failed}",
                @event: "liveedit.deletefailed", file: path);
        }
        else
        {
            StoreText.Text = $"Deleted {System.IO.Path.GetFileName(path)}.";

            ActivityLog.Shared.Ok(LogSource.App,
                $"DELETE removed {gone.Count} unreadable file(s), " +
                $"{bytes / (double)(1L << 20):F0} MB: {System.IO.Path.GetFileName(path)}.",
                @event: "liveedit.deleted", file: path,
                detail: string.Join("\n", gone));
        }

        _monitoring = null;
        MonitorText.Text = "";
        MonitorDeleteButton.Visibility = Visibility.Collapsed;
        MonitorPlaceholder.Text = "select a file to preview it";

        // The lists and the timeline both held that file.
        _signature = "";
        if (_showingIngest) LoadIngest(); else LoadClips();
    }

    /// <summary>True while the recorder still has this file open.</summary>
    private bool Pending(string path) =>
        _placed.Any(p => p.Pending &&
                         string.Equals(p.Clip.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>The master beside a proxy, if this is a proxy and its master is on disk.</summary>
    private string? MasterOf(string path) =>
        _placed.FirstOrDefault(p =>
            string.Equals(p.Clip.Path, path, StringComparison.OrdinalIgnoreCase))?.Clip.MasterPath;

    /// <summary>
    /// Every file that belongs to one piece of media: the media itself first, then whatever was
    /// written beside it.
    ///
    /// Only files that exist, so the caller's count and total are the truth rather than a list
    /// of things it intends to try.
    /// </summary>
    private List<string> Companions(string path)
    {
        var all = new List<string> { path };

        // A capture proxy's master.
        if (MasterOf(path) is { } master) all.Add(master);

        // An export's playable copy and its metadata. Named by convention beside the export, so
        // they are found whether or not this window listed them.
        all.Add(System.IO.Path.ChangeExtension(path, ".json"));

        all.Add(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(path) ?? "",
            System.IO.Path.GetFileNameWithoutExtension(path) + RangeExport.PreviewSuffix));

        return all
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToList();
    }

    private static long Size(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}
