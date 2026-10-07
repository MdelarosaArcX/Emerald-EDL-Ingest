using System.IO;
using System.Windows;
using Emerald.Core;
using Emerald.Media;

namespace Emerald.LiveEdit;

/// <summary>
/// Asks for the two things only the operator knows about an export — what to call it and what it
/// is — and shows them what the marks actually came to before they commit.
///
/// It does not do the export. The cut shells out to ffmpeg and can take a while on a long range,
/// so the dialog closes on OK and the caller runs the work off the dispatcher. A dialog that sat
/// there frozen would be indistinguishable from the app having hung.
/// </summary>
public partial class ExportDialog : Window
{
    /// <summary>The name the operator settled on, without an extension.</summary>
    public string ExportName { get; private set; } = "";

    /// <summary>Whatever they wrote about it. Empty is allowed; most exports are self-evident.</summary>
    public string Metadata { get; private set; } = "";

    private readonly string _folder;
    private readonly string _extension;

    public ExportDialog(string suggestedName, string extension, Timecode inAt, Timecode outAt,
                        int rate, IReadOnlyList<RangePart> parts, string storeRoot)
    {
        InitializeComponent();

        _folder = RangeExport.FolderFor(storeRoot);
        _extension = extension;

        long frames = parts.Sum(p => p.Frames);

        RangeText.Text = $"{inAt} - {outAt}   {new Timecode(frames, rate)}   {frames:N0} frames";

        PartsText.Text = parts.Count switch
        {
            0 => "The marked range covers no media.",
            1 => $"From {Path.GetFileName(parts[0].Path)}.",
            _ => $"Across {parts.Count} segments, {Path.GetFileName(parts[0].Path)} " +
                 $"to {Path.GetFileName(parts[^1].Path)}.",
        };

        NameBox.Text = suggestedName;
        ExportButton.IsEnabled = parts.Count > 0;

        // The name is the field they are most likely to change, and selecting it means a
        // replacement is one keystroke rather than a drag.
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();

        if (name.Length == 0)
        {
            Complain("Give the export a name.");
            return;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Complain("A name cannot contain \\ / : * ? \" < > |.");
            return;
        }

        // Refuse rather than overwrite. An export is somebody's work and the name is a guess
        // we suggested, so silently replacing one would be ours to answer for.
        if (File.Exists(Path.Combine(_folder, $"{name}.{_extension}")))
        {
            Complain($"{name}.{_extension} is already in the ingest folder. Choose another name.");
            return;
        }

        ExportName = name;
        Metadata = MetadataBox.Text.Trim();
        DialogResult = true;
    }

    private void Complain(string problem)
    {
        ProblemText.Text = problem;
        ProblemText.Visibility = Visibility.Visible;
        NameBox.Focus();
    }
}
