using System.Windows;
using System.Windows.Controls;
using Emerald.Core;
using Emerald.Deltacast;

namespace Emerald.LiveEdit;

/// <summary>
/// What going live needs to know: which board, which TX ports, how far behind, and how much the
/// delay may grow.
///
/// The first three are what the playback deck asks before its own tidal lock. The fourth is new
/// to this, because Live Edit's transmission is the one that plays inserts: each one holds the
/// recording back for its length, the delay buffer is a ring of fixed size, and so the room for
/// them has to be decided now rather than discovered on air.
/// </summary>
public partial class GoLiveDialog : Window
{
    public uint BoardIndex { get; private set; }
    public IReadOnlyList<int> Ports { get; private set; } = Array.Empty<int>();
    public TimeSpan Delay { get; private set; }
    public DelayStart Start { get; private set; }
    public TimeSpan Headroom { get; private set; }

    private readonly AppSettings _settings;
    private readonly int _rate;

    private sealed record Choice(TimeSpan Value, string Label, DelayStart Start = DelayStart.Fixed)
    {
        public override string ToString() => Label;
    }

    private static readonly Choice[] Delays =
    {
        new(TimeSpan.Zero, "no delay"),
        new(TimeSpan.FromSeconds(30), "30 s"),
        new(TimeSpan.FromMinutes(1), "1 min"),
        new(TimeSpan.FromMinutes(2), "2 min"),
        new(TimeSpan.FromMinutes(5), "5 min"),
        new(TimeSpan.FromMinutes(10), "10 min"),

        // From the top of the recording rather than a set time behind it: the delay is settled
        // when the capture deck hands over the line, from how much is recorded by then.
        new(TimeSpan.Zero, "from first segment", DelayStart.FromFirstSegment),
        new(TimeSpan.Zero, "when first segment is ready", DelayStart.WhenFirstSegmentReady),
    };

    private static readonly Choice[] Headrooms =
    {
        new(TimeSpan.Zero, "none - no inserts"),
        new(TimeSpan.FromMinutes(1), "1 min"),
        new(TimeSpan.FromMinutes(2), "2 min"),
        new(TimeSpan.FromMinutes(5), "5 min"),
        new(TimeSpan.FromMinutes(10), "10 min"),
        new(TimeSpan.FromMinutes(15), "15 min"),
        new(TimeSpan.FromMinutes(30), "30 min"),
    };

    public GoLiveDialog(AppSettings settings, int rate)
    {
        InitializeComponent();

        _settings = settings;
        _rate = rate;

        DelayBox.ItemsSource = Delays;
        DelayBox.SelectedItem = Delays.FirstOrDefault(d => d.Start == settings.LiveDelayStart &&
                                                           (d.Start != DelayStart.Fixed || (int)d.Value.TotalSeconds == settings.LiveDelaySeconds))
                                ?? Delays[3];

        HeadroomBox.ItemsSource = Headrooms;
        HeadroomBox.SelectedItem = Headrooms.FirstOrDefault(h => (int)h.Value.TotalSeconds == settings.LiveHeadroomSeconds)
                                   ?? Headrooms[3];

        GoButton.IsEnabled = false;
        PortsNote.Text = "scanning boards...";

        Loaded += async (_, _) => ShowBoards(await BoardService.ScanAsync());
    }

    private void ShowBoards(BoardScanResult scan)
    {
        List<BoardInfo> boards = scan.Boards.Where(b => b.TxCount > 0).ToList();

        BoardBox.ItemsSource = boards;

        if (boards.Count == 0)
        {
            PortsNote.Text = scan.Error ?? "No board with a TX port was found.";
            return;
        }

        BoardBox.SelectedItem = boards.FirstOrDefault(b => b.Index == _settings.LiveBoardIndex) ?? boards[0];
    }

    private void Board_Changed(object sender, SelectionChangedEventArgs e)
    {
        PortPanel.Children.Clear();

        if (BoardBox.SelectedItem is not BoardInfo board) return;

        HashSet<string> chosen = _settings.LivePorts
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var busy = new List<string>();

        foreach (ChannelPort port in board.TxPorts)
        {
            // A port another transmission is sending on is shown, and cannot be ticked: its
            // picture is somebody else's on air.
            string? heldBy = SdiPorts.Shared.HeldBy(new SdiPort(board.Index, port.Index));
            bool mine = heldBy is null || heldBy.StartsWith("live edit", StringComparison.OrdinalIgnoreCase);

            var box = new CheckBox
            {
                Content = port.Name,
                Tag = port.Index,
                Margin = new Thickness(0, 0, 14, 4),
                Foreground = (System.Windows.Media.Brush)FindResource(mine ? "Text" : "Muted"),
                IsEnabled = mine,
                IsChecked = mine && chosen.Contains(port.Name),
                ToolTip = mine ? null : $"In use by {heldBy}",
            };

            box.Click += (_, _) => ShowSummary();

            if (!mine) busy.Add($"{port.Name} ({heldBy})");

            PortPanel.Children.Add(box);
        }

        // The first free port, when nothing remembered is available - going live with nothing
        // ticked is a question, not a choice.
        if (!PortPanel.Children.OfType<CheckBox>().Any(b => b.IsChecked == true) &&
            PortPanel.Children.OfType<CheckBox>().FirstOrDefault(b => b.IsEnabled) is { } first)
            first.IsChecked = true;

        PortsNote.Text = busy.Count > 0 ? $"in use elsewhere: {string.Join(", ", busy)}" : "";

        ShowSummary();
    }

    private void Room_Changed(object sender, SelectionChangedEventArgs e) => ShowSummary();

    private List<int> ChosenPorts() => PortPanel.Children.OfType<CheckBox>()
        .Where(b => b.IsChecked == true)
        .Select(b => (int)b.Tag)
        .ToList();

    private void ShowSummary()
    {
        if (SummaryText is null) return;

        TimeSpan delay = (DelayBox.SelectedItem as Choice)?.Value ?? TimeSpan.Zero;
        TimeSpan room = (HeadroomBox.SelectedItem as Choice)?.Value ?? TimeSpan.Zero;

        // 1080 lines, 4:2:2, two bytes a pixel: what one frame of the ring costs.
        double bytesPerSecond = 1920.0 * 1080 * 2 * _rate;
        double gb = (delay + room).TotalSeconds * bytesPerSecond / 1e9;

        List<int> ports = ChosenPorts();

        DelayStart start = (DelayBox.SelectedItem as Choice)?.Start ?? DelayStart.Fixed;

        SummaryText.Text =
            (start == DelayStart.Fixed
                ? $"delay {TidalLock.Describe(delay)}, may grow to {TidalLock.Describe(delay + room)} with inserts\n"
                : $"starts {TidalLock.DescribeStart(start, delay)}; the delay is however much is recorded " +
                  $"by then, and may grow by {TidalLock.Describe(room)} with inserts\n") +
            $"delay buffer about {gb:0.#} GB on disk\n" +
            (ports.Count == 0
                ? "no TX port chosen"
                : $"out of {string.Join(", ", ports.Select(p => $"TX{p}"))}");

        GoButton.IsEnabled = BoardBox.SelectedItem is BoardInfo && ports.Count > 0;
    }

    private void Go_Click(object sender, RoutedEventArgs e)
    {
        if (BoardBox.SelectedItem is not BoardInfo board) return;

        List<int> ports = ChosenPorts();
        if (ports.Count == 0) return;

        BoardIndex = board.Index;
        Ports = ports;
        Delay = (DelayBox.SelectedItem as Choice)?.Value ?? TimeSpan.Zero;
        Start = (DelayBox.SelectedItem as Choice)?.Start ?? DelayStart.Fixed;
        Headroom = (HeadroomBox.SelectedItem as Choice)?.Value ?? TimeSpan.Zero;

        _settings.LiveBoardIndex = board.Index;
        _settings.LivePorts = string.Join("|", board.TxPorts.Where(p => ports.Contains(p.Index)).Select(p => p.Name));
        _settings.LiveDelaySeconds = (int)Delay.TotalSeconds;
        _settings.LiveDelayStart = Start;
        _settings.LiveHeadroomSeconds = (int)Headroom.TotalSeconds;
        _settings.Save();

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
