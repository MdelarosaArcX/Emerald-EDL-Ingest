using System.Windows;
using Emerald.Core;

namespace Emerald.LiveEdit;

/// <summary>
/// A sound track has been chosen: when should it start playing? Instantly, on the mark in, or from a
/// timecode.
///
/// "Instantly" is worked out when the button is pressed, not when this opened, so however long it is
/// left on screen the change is never behind air.
/// </summary>
public partial class AudioTrackDialog : Window
{
    private readonly int _rate;

    /// <summary>True for instantly.</summary>
    public bool Instantly { get; private set; }

    /// <summary>True for on the mark in; with neither, <see cref="At"/> is the timecode typed.</summary>
    public bool OnMarkIn { get; private set; }

    public Timecode At { get; private set; }

    /// <param name="track">What was chosen, as it is named on its lane.</param>
    /// <param name="onAir">True when instantly means on air, and not only in this window.</param>
    /// <param name="markIn">The mark in as a timecode, or null when there is none.</param>
    /// <param name="markAired">True when air has reached the mark in, or is about to.</param>
    /// <param name="timecode">What the timecode starts as: where the click was.</param>
    public AudioTrackDialog(string track, bool onAir, string? markIn, bool markAired, string timecode, int rate)
    {
        InitializeComponent();

        _rate = rate;
        HeadingText.Text = $"PLAY {track}";
        TimecodeBox.Text = timecode;

        NowText.Text = onAir
            ? "On air within about a second, and in the preview."
            : "From the playhead. Nothing is on air; it goes to air from that point once you are live.";

        MarkButton.IsEnabled = markIn is not null && !markAired;
        MarkText.Text = markIn is null ? "No mark in is set. Press I at the playhead to set one."
                      : markAired ? $"The mark in, {markIn}, has gone to air or is about to."
                      : $"From {markIn}, until the next audio change on the timeline.";

        Loaded += (_, _) =>
        {
            TimecodeBox.Focus();
            TimecodeBox.SelectAll();
        };
    }

    private void Now_Click(object sender, RoutedEventArgs e)
    {
        Instantly = true;
        DialogResult = true;
    }

    private void Mark_Click(object sender, RoutedEventArgs e)
    {
        OnMarkIn = true;
        DialogResult = true;
    }

    private void At_Click(object sender, RoutedEventArgs e)
    {
        if (!Timecode.TryParse(TimecodeBox.Text.Trim(), _rate, out Timecode at, out string? problem))
        {
            ProblemText.Text = problem;
            ProblemText.Visibility = Visibility.Visible;
            TimecodeBox.Focus();
            return;
        }

        At = at;
        DialogResult = true;
    }
}
