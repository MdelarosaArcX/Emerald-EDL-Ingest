using System.IO;
using System.Windows;
using Emerald.Core;

namespace Emerald.LiveEdit;

/// <summary>
/// Asks how a dropped piece of ingest media should sit against the recording.
///
/// The four options do genuinely different things — three cover the recording and one lengthens
/// it, two take their span from the marked range and two from where the pointer was, and only
/// one changes the speed — so each is described in full rather than named, and the effect of the
/// chosen one at this particular length is worked out and shown before anything is committed.
/// </summary>
public partial class IngestDropDialog : Window
{
    /// <summary>The plan the operator settled on. Only meaningful once the dialog returned true.</summary>
    public IngestPlan Plan { get; private set; }

    private readonly long _ingestFrames;
    private readonly long _dropFrame;
    private readonly long? _markIn, _markOut;
    private readonly int _rate;

    public IngestDropDialog(string path, long ingestFrames, long dropFrame,
                            long? markIn, long? markOut, int rate)
    {
        InitializeComponent();

        _ingestFrames = ingestFrames;
        _dropFrame = dropFrame;
        _markIn = markIn;
        _markOut = markOut;
        _rate = rate;

        WhatText.Text =
            $"{Path.GetFileName(path)}\n" +
            $"{ingestFrames:N0} frames ({new Timecode(ingestFrames, rate)})   " +
            $"dropped at {new Timecode(dropFrame, rate)}\n" +
            (markIn is { } a && markOut is { } b && b > a
                ? $"marked range {new Timecode(a, rate)} - {new Timecode(b, rate)} " +
                  $"({b - a:N0} frames)"
                : "no marked range - fit to fill and replace need one");

        ShowEffect();
    }

    private IngestMode Chosen =>
        FitRadio.IsChecked == true ? IngestMode.FitToFill
        : ReplaceRadio.IsChecked == true ? IngestMode.Replace
        : InsertRadio.IsChecked == true ? IngestMode.Insert
        : IngestMode.Overlay;

    private void Mode_Changed(object sender, RoutedEventArgs e) => ShowEffect();

    /// <summary>
    /// Works the chosen option out against this file and this range, and says so.
    ///
    /// Also what disables Place: an option that cannot be planned — fit to fill with no marked
    /// range — says why rather than being allowed and failing afterwards.
    /// </summary>
    private void ShowEffect()
    {
        if (!IngestPlacement.TryPlan(Chosen, _ingestFrames, _dropFrame, _markIn, _markOut,
                                     out IngestPlan plan, out string? problem))
        {
            EffectText.Text = problem ?? "That cannot be placed.";
            EffectText.Foreground = (System.Windows.Media.Brush)FindResource("Bad");

            WarnText.Visibility = Visibility.Collapsed;
            PlaceButton.IsEnabled = false;
            return;
        }

        Plan = plan;

        EffectText.Foreground = (System.Windows.Media.Brush)FindResource("Text");
        EffectText.Text =
            $"{new Timecode(plan.StartFrame, _rate)} - {new Timecode(plan.EndFrame, _rate)}: " +
            IngestPlacement.Describe(plan, _rate) + ".";

        // The two consequences worth a second line, because both change what goes to air beyond
        // the item itself.
        string? warn =
            plan.ShiftsAfterBy > 0
                ? $"Everything after {new Timecode(plan.StartFrame, _rate)} moves " +
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

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Place_Click(object sender, RoutedEventArgs e)
    {
        // Re-planned rather than trusting what the last ShowEffect left behind, so what is
        // returned is certainly the option currently selected.
        if (!IngestPlacement.TryPlan(Chosen, _ingestFrames, _dropFrame, _markIn, _markOut,
                                     out IngestPlan plan, out _))
            return;

        Plan = plan;
        DialogResult = true;
    }
}
