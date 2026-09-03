using System.Collections.ObjectModel;
using System.Windows;

namespace Emerald.Ingest;

/// <summary>
/// Every ingest that has finished, however it finished.
///
/// Completed, cancelled and failed jobs are all here together and none of them is hidden.
/// A history that only showed the successes would be worse than no history: the question an
/// operator actually asks it is "what happened to the 21:30", and the answer is usually one
/// of the other two.
/// </summary>
public partial class IngestHistoryWindow : Window
{
    private readonly IIngestControllerService _controller;
    private readonly ObservableCollection<IngestHistoryRow> _rows = new();

    public IngestHistoryWindow(IIngestControllerService controller)
    {
        _controller = controller;

        InitializeComponent();
        HistoryList.ItemsSource = _rows;

        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        IReadOnlyList<IngestJob> jobs = _controller.History();

        _rows.Clear();
        foreach (IngestJob job in jobs) _rows.Add(IngestHistoryRow.From(job));

        EmptyText.Visibility = jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int completed = jobs.Count(j => j.Status == IngestStatus.Completed);
        int failed = jobs.Count(j => j.Status == IngestStatus.Failed);
        int cancelled = jobs.Count(j => j.Status == IngestStatus.Cancelled);

        SummaryText.Text = $"{jobs.Count} finished job(s) - " +
                           $"{completed} completed, {cancelled} cancelled, {failed} failed";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Load();
}
