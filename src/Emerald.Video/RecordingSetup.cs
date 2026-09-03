using System.IO;
using Emerald.Core;

namespace Emerald.Video;

/// <summary>Everything <see cref="SdiCapture"/> needs to record one receiver.</summary>
/// <param name="FrameLimit">
/// Stop after this many frames have been taken off the receiver, or null to record until
/// stopped. Counting frames rather than watching a wall clock is what makes an ingest end
/// on the frame it was asked to: the receiver's own cadence is the only clock that agrees
/// with what is being written.
/// </param>
/// <param name="SingleFile">
/// True to write one file per output, named exactly after <paramref name="NamePrefix"/>,
/// instead of the timestamped segments a continuous capture produces. An ingest is one
/// clip with a name the operator chose, so it cannot be handed back as twelve segments.
/// </param>
public sealed record CaptureRequest(
    string FfmpegPath,
    uint BoardIndex,
    int RxChannel,
    string Folder,
    int FrameRate,
    string NamePrefix,
    RecordingProfile Profile,
    long? FrameLimit = null,
    bool SingleFile = false,
    string? StartTimecode = null)
{
    /// <summary>The files this request will write, proxy first — the order ffmpeg is given them in.</summary>
    public IReadOnlyList<string> OutputPaths => SingleFile
        ? RecordingProfile.Outputs
            .Select(o => Path.Combine(RecordingProfile.FolderFor(o, Folder), $"{NamePrefix}.{o.Extension}"))
            .ToList()
        : Array.Empty<string>();
}

/// <summary>
/// Assembles a recording from the operator's settings, and says why it cannot when it
/// cannot.
///
/// Both places that record — the capture deck's Record button and the EDL, which records
/// while a message is on air — come through here, so a missing ffmpeg, a folder that is not
/// there or a codec the container will not hold is reported the same way and caught before
/// the receiver is opened.
/// </summary>
public static class RecordingSetup
{
    public static bool TryBuild(
        AppSettings settings,
        uint boardIndex,
        int rxChannel,
        string folder,
        int frameRate,
        string namePrefix,
        out CaptureRequest? request,
        out string? problem,
        long? frameLimit = null,
        bool singleFile = false,
        string? startTimecode = null)
    {
        request = null;
        folder = folder.Trim();

        if (folder.Length == 0)
        {
            problem = "no recording folder is set.";
            return false;
        }

        if (!Directory.Exists(folder))
        {
            problem = $"{folder} does not exist.";
            return false;
        }

        if (Ffmpeg.Locate(settings.FfmpegPath) is not { } ffmpeg)
        {
            problem = "ffmpeg was not found - recording needs it to encode.";
            return false;
        }

        if (rxChannel < 0)
        {
            problem = "no RX port is selected.";
            return false;
        }

        RecordingProfile profile = RecordingProfile.From(settings);

        // Both files are written under the folder the operator chose, one per output.
        try
        {
            foreach (string output in RecordingProfile.FoldersFor(folder)) Directory.CreateDirectory(output);
        }
        catch (Exception ex)
        {
            problem = $"cannot prepare {folder}: {ex.Message}";
            return false;
        }

        // Sanitised here rather than only inside the recorder, so a caller that needs to know
        // where the files will land — an ingest checking it is not about to overwrite a clip —
        // reads the same names ffmpeg is given.
        request = new CaptureRequest(
            FfmpegPath: ffmpeg,
            BoardIndex: boardIndex,
            RxChannel: rxChannel,
            Folder: folder,
            FrameRate: frameRate > 0 ? frameRate : 25,
            NamePrefix: SdiCapture.SanitisePrefix(namePrefix),
            Profile: profile,
            FrameLimit: frameLimit,
            SingleFile: singleFile,
            StartTimecode: startTimecode);

        problem = null;
        return true;
    }
}
