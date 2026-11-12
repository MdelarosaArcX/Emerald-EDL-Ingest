using System.IO;

namespace Emerald.Video;

/// <summary>
/// Where a recording's two outputs go: the DNxHD masters to <see cref="High"/> and the H.264
/// proxies to <see cref="Low"/>. Each can be anywhere, on a drive of its own: on a single
/// spinning disk the masters' 15 MB/s and every read of the proxies fight for the same head, and
/// putting the proxies on another drive is the cheapest way to stop that.
///
/// The layout before the folders could be chosen apart - one store with high\ and low\ inside
/// it - is <see cref="Under"/>, and every caller that still has one folder goes through it, so
/// nothing about such a store changes.
/// </summary>
public sealed record StorePaths(string High, string Low)
{
    /// <summary>The store as it always was: one folder, high\ and low\ inside it.</summary>
    public static StorePaths Under(string root) =>
        new(Path.Combine(root, RecordingProfile.HighRes.Folder), Path.Combine(root, RecordingProfile.LowRes.Folder));

    /// <summary>The folder an output's files go to.</summary>
    public string For(RecordingOutput output) =>
        output.Key == RecordingProfile.HighRes.Key ? High : Low;

    /// <summary>Both folders, proxy first, in the order <see cref="RecordingProfile.Outputs"/> lists them.</summary>
    public IEnumerable<string> All => RecordingProfile.Outputs.Select(For);

    /// <summary>
    /// The one folder both sit in, when they are laid out as <see cref="Under"/> lays them out;
    /// otherwise null. Loose files from before recordings had folders of their own are looked
    /// for here.
    /// </summary>
    public string? Root
    {
        get
        {
            string? high = Parent(High, RecordingProfile.HighRes.Folder);
            string? low = Parent(Low, RecordingProfile.LowRes.Folder);

            return high is not null && low is not null &&
                   string.Equals(Path.GetFullPath(high), Path.GetFullPath(low), StringComparison.OrdinalIgnoreCase)
                ? high
                : null;
        }
    }

    /// <summary>
    /// Where Live Edit's exports and the deck's snapshots go: ingest\ beside high\ and low\ in a
    /// store laid out as it always was, so existing exports are still found; otherwise ingest\
    /// inside the high-res folder, since an export is cut from the masters.
    /// </summary>
    public string Exports => Path.Combine(Root ?? High, "ingest");

    /// <summary>The two folders are on different drives.</summary>
    public bool Split =>
        !string.Equals(Path.GetPathRoot(Path.GetFullPath(High)), Path.GetPathRoot(Path.GetFullPath(Low)),
                       StringComparison.OrdinalIgnoreCase);

    private static string? Parent(string folder, string name)
    {
        try
        {
            string trimmed = Path.TrimEndingDirectorySeparator(folder);
            return string.Equals(Path.GetFileName(trimmed), name, StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(trimmed)
                : null;
        }
        catch (ArgumentException) { return null; }
    }
}
