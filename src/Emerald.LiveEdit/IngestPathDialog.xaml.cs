using System.IO;
using System.Windows;
using Emerald.Core;
using Emerald.Media;
using Emerald.Video;

namespace Emerald.LiveEdit;

/// <summary>
/// Where ingest media is kept. Blank means the store's ingest folder, as before there was a
/// setting. Never the high-res or low-res recording folder itself: the storage warden deletes old
/// recordings there, and would take ingest media with them.
/// </summary>
public partial class IngestPathDialog : Window
{
    /// <summary>What was chosen: a full path, or "" for the default.</summary>
    public string ChosenPath { get; private set; } = "";

    private readonly StorePaths _store;

    public IngestPathDialog(string current, StorePaths store)
    {
        InitializeComponent();

        _store = store;
        PathBox.Text = current;
        HintText.Text = $"Blank uses the default, {store.Exports}.";

        Loaded += (_, _) =>
        {
            PathBox.Focus();
            PathBox.SelectAll();
        };
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where ingest media is kept" };

        string typed = PathBox.Text.Trim().Trim('"');
        if (typed.Length > 0 && Directory.Exists(typed)) dialog.InitialDirectory = typed;

        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FolderName;
    }

    private void Default_Click(object sender, RoutedEventArgs e) => PathBox.Text = "";

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string typed = PathBox.Text.Trim().Trim('"');

        if (typed.Length == 0)
        {
            ChosenPath = "";
            DialogResult = true;
            return;
        }

        string full;

        try
        {
            full = Path.GetFullPath(typed).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Complain($"That is not a folder path: {ex.Message}");
            return;
        }

        foreach (string recording in new[] { _store.High, _store.Low })
        {
            if (Same(full, recording))
            {
                Complain($"{full} is a recording folder. Old recordings are deleted from there to make room, " +
                         "and ingest media would go with them. Choose another folder.");
                return;
            }
        }

        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Complain($"{full} could not be made: {ex.Message}");
            return;
        }

        ChosenPath = full;
        DialogResult = true;
    }

    private static bool Same(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void Complain(string problem)
    {
        ProblemText.Text = problem;
        ProblemText.Visibility = Visibility.Visible;
        PathBox.Focus();
    }
}
