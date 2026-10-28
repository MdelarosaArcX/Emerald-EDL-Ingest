using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Emerald.Core;

namespace Emerald.LiveEdit;

/// <summary>
/// The recording as it comes in, on the big screen.
///
/// <b>Why it is not played from the file.</b> The segment being recorded cannot be opened until
/// the recorder closes it - an MP4 has no index until then - so the newest few minutes of the
/// day are exactly the part the timeline cannot show. The capture deck already makes a preview
/// of every frame it records for its own monitor; this shows that same picture, through
/// <see cref="RecordingPicture"/>, so it is the live input and not a file.
///
/// Picture only: the recorder's audio goes to the capture deck's speakers, not here.
/// </summary>
public partial class LiveEditWindow
{
    private IDisposable? _recordingWatch;
    private WriteableBitmap? _recordingBitmap;
    private long _recordingSeen = -1;
    private DateTime _recordingFrameAt;

    private bool InRecordingView => _recordingWatch is not null;

    private void RecordingView_Click(object sender, RoutedEventArgs e) => ToggleRecordingView();

    private void ToggleRecordingView()
    {
        if (InRecordingView) { LeaveRecordingView(); return; }

        if (!PipelineState.Shared.Recording)
        {
            StoreText.Text = "Nothing is being recorded - start the capture deck to watch the recording come in.";
            return;
        }

        // The timeline stops: this screen is showing something else now, and its sound would be
        // from somewhere other than the picture.
        Pause();

        _recordingWatch = RecordingPicture.Shared.Watch();
        _recordingSeen = -1;
        _recordingFrameAt = DateTime.UtcNow;

        RecordingViewText.Text = "● REC";
        RecordingView.Visibility = Visibility.Visible;
        RecordingViewButton.Content = "■ REC";
        RecordingViewButton.Foreground = Brush("Bad");

        ActivityLog.Shared.Info(LogSource.LiveEdit,
            "REC VIEW  Live Edit's big screen is showing the recording as it comes in.",
            @event: "liveedit.recview");
    }

    /// <summary>Back to the timeline, on the frame it was on. Harmless when not in the view.</summary>
    private void LeaveRecordingView()
    {
        if (_recordingWatch is null) return;

        _recordingWatch.Dispose();
        _recordingWatch = null;

        RecordingView.Visibility = Visibility.Collapsed;
        RecordingImage.Source = null;
        _recordingBitmap = null;

        RecordingViewButton.Content = "● REC";
        RecordingViewButton.ClearValue(ForegroundProperty);
    }

    /// <summary>Every tick: the newest frame the recorder has made, if there is one.</summary>
    private void ShowRecordingView()
    {
        if (!InRecordingView) return;

        if (!PipelineState.Shared.Recording)
        {
            LeaveRecordingView();
            StoreText.Text = "The recording stopped - the big screen is back on the timeline.";
            return;
        }

        bool drew = RecordingPicture.Shared.TryRead(ref _recordingSeen, (bgra, width, height, frame) =>
        {
            if (_recordingBitmap is null || _recordingBitmap.PixelWidth != width ||
                _recordingBitmap.PixelHeight != height)
            {
                _recordingBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                RecordingImage.Source = _recordingBitmap;
            }

            _recordingBitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);

            RecordingViewText.Text = "● REC  " +
                (PipelineState.TimecodeOf(PipelineState.Shared.RecordingStart, Math.Max(0, frame))?.ToString()
                 ?? new Timecode(Math.Max(0, frame), _rate).ToString());
        });

        if (drew)
            _recordingFrameAt = DateTime.UtcNow;
        else if (DateTime.UtcNow - _recordingFrameAt > TimeSpan.FromSeconds(2))
            RecordingViewText.Text = "● REC  waiting for the recorder's picture";
    }
}
