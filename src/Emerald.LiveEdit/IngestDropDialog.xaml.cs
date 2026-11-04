using System.IO;
using System.Windows;
using System.Windows.Controls;
using Emerald.Core;

namespace Emerald.LiveEdit;

/// <summary>
/// Asks how a dropped piece of ingest media should sit against the recording, and where.
///
/// The four options do genuinely different things — three cover the recording and one lengthens
/// it, two take their span from the marked range and two from where the pointer was, and only
/// one changes the speed — so each is described in full rather than named, and the effect of the
/// chosen one at this particular length is worked out and shown before anything is committed.
///
/// Where it starts is typed rather than only taken from the pointer: a drop is as accurate as a
/// mouse, and the operator usually has the timecode it should go at in front of them.
/// </summary>
public partial class IngestDropDialog : Window
{
    /// <summary>The plan the operator settled on. Only meaningful once the dialog returned true.</summary>
    public IngestPlan Plan { get; private set; }

    /// <summary>The timeline frame the item starts at. Only meaningful once the dialog returned true.</summary>
    public long RulerStart { get; private set; }

    private readonly long _ingestFrames;
    private readonly long _dropFrame;
    private readonly long? _markIn, _markOut;
    private readonly int _rate;
    private readonly long _dayFrames;
    private readonly Func<long, long> _captured;
    private readonly Func<long, string?> _refused;
    private readonly bool _ready;

    /// <summary>The start last typed, kept while a range mode has the box showing mark in.</summary>
    private string _typed = "";

    /// <param name="dropFrame">The timeline frame the file was dropped at - where it goes unless another is typed.</param>
    /// <param name="markIn">Mark in, in timeline frames.</param>
    /// <param name="markOut">Mark out, in timeline frames.</param>
    /// <param name="captured">Turns a timeline frame into the recording's own frame, which is what a plan is in.</param>
    /// <param name="refused">Why nothing may start at a timeline frame, or null when something may.</param>
    public IngestDropDialog(string path, long ingestFrames, long dropFrame,
                            long? markIn, long? markOut, int rate, long dayFrames,
                            Func<long, long> captured, Func<long, string?> refused,
                            bool hasVideo = true, bool hasAudio = true)
    {
        InitializeComponent();

        // A file with only one track can only go out as that track.
        if (!hasVideo || !hasAudio)
        {
            BothRadio.IsEnabled = false;
            VideoOnlyRadio.IsEnabled = hasVideo;
            AudioOnlyRadio.IsEnabled = hasAudio;

            (hasVideo ? VideoOnlyRadio : AudioOnlyRadio).IsChecked = true;
        }

        _ingestFrames = ingestFrames;
        _dropFrame = dropFrame;
        _markIn = markIn;
        _markOut = markOut;
        _rate = rate;
        _dayFrames = dayFrames;
        _captured = captured;
        _refused = refused;

        bool ranged = markIn is { } from && markOut is { } to && to > from;

        WhatText.Text =
            $"{Path.GetFileName(path)}\n" +
            $"{ingestFrames:N0} frames ({new Timecode(ingestFrames, rate)})   " +
            $"dropped at {Stamp(dropFrame)}\n" +
            (ranged
                ? $"marked range {Stamp(markIn!.Value)} - {Stamp(markOut!.Value)} " +
                  $"({markOut.Value - markIn.Value:N0} frames)"
                : "no marked range - fit to fill and replace need one");

        // Overlay stays selected, whatever is marked.
        //
        // A marked range does not decide what the drop meant. Marks are set for all sorts of
        // reasons - an export, a note of where something starts - and a drop while they happen
        // to be set is not a request to use them. Choosing Fit to fill or Replace is what moves
        // the item onto the marks; until then it goes where it was dropped, which is the thing
        // the operator actually did with the pointer.
        //
        // An earlier version pre-selected Fit to fill whenever a range existed. That put a
        // speed change one unread keystroke away from being committed, on the strength of
        // guessing at intent.

        // The two range modes cannot be planned without a range, so they are not offered without
        // one. Their descriptions stay readable, which is how an operator learns what a mark
        // would buy them.
        FitRadio.IsEnabled = ranged;
        ReplaceRadio.IsEnabled = ranged;

        StartBox.Text = Stamp(dropFrame);

        _ready = true;
        ShowEffect();

        Loaded += (_, _) =>
        {
            StartBox.Focus();
            StartBox.SelectAll();
        };
    }

    private IngestMode Chosen =>
        FitRadio.IsChecked == true ? IngestMode.FitToFill
        : ReplaceRadio.IsChecked == true ? IngestMode.Replace
        : InsertRadio.IsChecked == true ? IngestMode.Insert
        : IngestMode.Overlay;

    private IngestTracks Tracks =>
        VideoOnlyRadio.IsChecked == true ? IngestTracks.VideoOnly
        : AudioOnlyRadio.IsChecked == true ? IngestTracks.AudioOnly
        : IngestTracks.Both;

    /// <summary>The two modes whose start is mark in, not the typed timecode.</summary>
    private bool StartsAtMark => Chosen is IngestMode.FitToFill or IngestMode.Replace;

    /// <summary>A timeline frame as the ruler shows it: time of day, so past midnight wraps.</summary>
    private string Stamp(long frame)
    {
        long oneDay = 86_400L * _rate;
        return new Timecode((frame % oneDay + oneDay) % oneDay, _rate).ToString();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e) => ShowEffect();

    private void Start_Changed(object sender, TextChangedEventArgs e) => ShowEffect();

    private void DropPoint_Click(object sender, RoutedEventArgs e) => StartBox.Text = Stamp(_dropFrame);

    /// <summary>
    /// Where the item would start on the timeline, or null with the reason: the typed timecode,
    /// or mark in for the two modes that take their place from the marks.
    /// </summary>
    private long? Start(out string? problem)
    {
        problem = null;

        if (StartsAtMark) return _markIn;

        if (!FramePick.TryParse(StartBox.Text, _rate, _dayFrames, _dropFrame,
                                out long frame, out problem))
        {
            problem ??= "That is not a timecode.";
            return null;
        }

        return frame;
    }

    /// <summary>
    /// Works the chosen option out against this file, this start and this range, and says so.
    ///
    /// Also what disables Place: an option that cannot be planned — fit to fill with no marked
    /// range, a start inside the no edit zone, a timecode that is not one — says why rather than
    /// being allowed and failing afterwards.
    /// </summary>
    private void ShowEffect()
    {
        // Radio buttons and the text box raise their events while InitializeComponent is still
        // building the dialog, before there is anything to plan against.
        if (!_ready) return;

        // The typed start does not apply to the range modes, so for them the box shows mark in
        // and cannot be typed in; going back to Overlay or Insert gives back what was typed.
        bool wasLocked = !StartBox.IsEnabled;

        if (StartsAtMark && !wasLocked) _typed = StartBox.Text;

        StartBox.IsEnabled = !StartsAtMark;
        DropPointButton.IsEnabled = !StartsAtMark;
        StartHint.Text = StartsAtMark ? "starts at mark in" : "on the timeline";

        if (StartsAtMark && _markIn is { } markIn && StartBox.Text != Stamp(markIn))
        {
            StartBox.Text = Stamp(markIn);
            return; // TextChanged has already shown the effect
        }

        if (wasLocked && !StartsAtMark && StartBox.Text != _typed)
        {
            StartBox.Text = _typed;
            return;
        }

        if (!TryPlan(out IngestPlan plan, out long start, out string? problem))
        {
            EffectText.Text = problem ?? "That cannot be placed.";
            EffectText.Foreground = (System.Windows.Media.Brush)FindResource("Bad");

            WarnText.Visibility = Visibility.Collapsed;
            PlaceButton.IsEnabled = false;
            return;
        }

        Plan = plan;
        RulerStart = start;

        EffectText.Foreground = (System.Windows.Media.Brush)FindResource("Text");
        EffectText.Text =
            $"{Stamp(start)} - {Stamp(start + plan.Frames)}: " +
            IngestPlacement.Describe(plan, _rate) + ".";

        // The two consequences worth a second line, because both change what goes to air beyond
        // the item itself.
        string? warn =
            plan.ShiftsAfterBy > 0
                ? $"Everything after {Stamp(start)} moves " +
                  $"{plan.ShiftsAfterBy:N0} frames later, so the delay grows by " +
                  $"{new Timecode(plan.ShiftsAfterBy, _rate)}."
                : plan.Stretched
                    ? $"The media plays at {plan.Speed:0.###} times its recorded speed, so its " +
                      "pitch and motion change."
                    : null;

        WarnText.Text = warn ?? "";
        WarnText.Visibility = warn is null ? Visibility.Collapsed : Visibility.Visible;

        PlaceButton.IsEnabled = true;
    }

    /// <summary>The chosen option planned at the chosen start, in the recording's own frames.</summary>
    private bool TryPlan(out IngestPlan plan, out long start, out string? problem)
    {
        plan = default!;
        start = 0;

        if (Start(out problem) is not { } at) return false;

        if (_refused(at) is { } refusal)
        {
            problem = refusal;
            return false;
        }

        start = at;

        return IngestPlacement.TryPlan(Chosen, _ingestFrames, _captured(at),
                                       _markIn is { } mIn ? _captured(mIn) : null,
                                       _markOut is { } mOut ? _captured(mOut) : null,
                                       out plan, out problem, Tracks);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Place_Click(object sender, RoutedEventArgs e)
    {
        // Re-planned rather than trusting what the last ShowEffect left behind, so what is
        // returned is certainly the option and the start currently shown.
        if (!TryPlan(out IngestPlan plan, out long start, out _))
            return;

        Plan = plan;
        RulerStart = start;
        DialogResult = true;
    }
}
