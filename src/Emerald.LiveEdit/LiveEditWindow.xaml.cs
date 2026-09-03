using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Emerald.Core;
using Emerald.Media;

namespace Emerald.LiveEdit;

/// <summary>
/// The editing workspace: a large viewer, a transport bar and a timeline under it.
///
/// The layout is real but the editing is not built yet — the transport and timeline are
/// inert. What is wired is the clip list, which reads the actual capture store through
/// <see cref="MediaLibrary"/> rather than showing invented rows, so the page reflects what
/// has genuinely been recorded and the store path can be checked at a glance.
/// </summary>
public partial class LiveEditWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();

    public LiveEditWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadClips();
    }

    private void LoadClips()
    {
        string folder = MediaLibrary.FolderFor(_settings);

        ClipList.ItemsSource = null;
        StoreText.Text = $"reading {folder}...";

        // Probing every clip shells out to ffprobe once per file, which is too slow for the
        // UI thread once the store has a few hours in it.
        Task.Run(() => MediaLibrary.List(_settings))
            .ContinueWith(t =>
            {
                IReadOnlyList<CapturedClip> clips = t.IsFaulted ? Array.Empty<CapturedClip>() : t.Result;

                ClipList.ItemsSource = clips;

                StoreText.Text = clips.Count == 0
                    ? $"No captures yet. Recordings land in {folder}."
                    : $"{clips.Count} clip{(clips.Count == 1 ? "" : "s")} in {folder}";

            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadClips();

    private void ClipList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClipList.SelectedItem is not CapturedClip clip)
        {
            PreviewPlaceholder.Text = "select a clip";
            PreviewPlaceholder.Visibility = Visibility.Visible;
            DrawTimeline(null);
            return;
        }

        PreviewPlaceholder.Text = $"{System.IO.Path.GetFileName(clip.Path)}\n\nplayback is not built yet";
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PositionText.Text = "00:00:00:00";

        DrawTimeline(clip);
    }

    private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e) =>
        DrawTimeline(ClipList.SelectedItem as CapturedClip);

    /// <summary>
    /// Ruler ticks and a single clip block, scaled to the selected clip's real duration.
    /// Drawing from the probed length rather than a fixed span means the timeline already
    /// tells the truth about what is loaded, before any editing exists to drive it.
    /// </summary>
    private void DrawTimeline(CapturedClip? clip)
    {
        RulerCanvas.Children.Clear();
        TrackCanvas.Children.Clear();

        double width = TrackCanvas.ActualWidth;
        if (width <= 1) return;

        double seconds = clip?.Info?.Duration.TotalSeconds ?? 0;
        if (seconds <= 0) return;

        // Aim for roughly one label every 90 px, snapped to a sane interval.
        double[] steps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
        double target = seconds / Math.Max(1, width / 90);
        double step = steps.FirstOrDefault(s => s >= target, 600);

        var line = (Brush)FindResource("Line");
        var muted = (Brush)FindResource("Muted");

        for (double t = 0; t <= seconds; t += step)
        {
            double x = t / seconds * width;

            RulerCanvas.Children.Add(new Line
            {
                X1 = x, X2 = x, Y1 = 14, Y2 = 24,
                Stroke = line, StrokeThickness = 1,
            });

            var label = new TextBlock
            {
                Text = TimeSpan.FromSeconds(t).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss"),
                Foreground = muted,
                FontSize = 10,
            };

            Canvas.SetLeft(label, x + 3);
            Canvas.SetTop(label, 1);
            RulerCanvas.Children.Add(label);
        }

        TrackCanvas.Children.Add(new Rectangle
        {
            Width = width,
            Height = Math.Max(24, TrackCanvas.ActualHeight - 24),
            RadiusX = 3,
            RadiusY = 3,
            Fill = new SolidColorBrush(Color.FromRgb(0x23, 0x36, 0x4E)),
            Stroke = (Brush)FindResource("Accent"),
            StrokeThickness = 1,
        });

        var name = new TextBlock
        {
            Text = clip!.Name,
            Foreground = (Brush)FindResource("Text"),
            FontSize = 11,
        };

        Canvas.SetLeft(name, 8);
        Canvas.SetTop(name, 8);
        TrackCanvas.Children.Add(name);

        Playhead.Margin = new Thickness(0);
    }
}
