using System.IO;
using Emerald.Video;

namespace Emerald.Ingest;

/// <summary>
/// What the destination disk can take.
///
/// An ingest is booked minutes or hours before it runs, so "is there room" has to be
/// answerable in advance, from the length and the raster, rather than discovered when the
/// encoder stops mid-clip.
/// </summary>
public static class DiskSpace
{
    /// <summary>Free bytes on the volume a path lives on, or null when it cannot be read.</summary>
    public static long? AvailableBytes(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            // A UNC path, a disconnected drive, a path that will not resolve: all mean the
            // same thing here, which is that no promise can be made about free space.
            return null;
        }
    }

    /// <summary>True when the folder exists and a file can actually be created in it.</summary>
    public static bool IsWritable(string? path, out string? problem)
    {
        problem = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            problem = "No recording directory is set.";
            return false;
        }

        if (!Directory.Exists(path))
        {
            problem = $"{path} does not exist.";
            return false;
        }

        // Asked rather than assumed: an ACL that denies writes looks exactly like a writable
        // folder until something tries, and an ingest is a bad place to find out.
        string probe = Path.Combine(path, $".emerald-ingest-{Guid.NewGuid():N}.tmp");

        try
        {
            using (FileStream stream = File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }

            return true;
        }
        catch (Exception ex)
        {
            problem = $"{path} cannot be written to: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Roughly how many bytes a recording of this many frames will occupy, across both files
    /// Emerald writes — the ProRes 422 master and the H.264 proxy beside it.
    ///
    /// ProRes is quality-driven, so this is an estimate and is deliberately generous: about
    /// 2.6 bits per pixel per frame, which lands near the 122 Mbps a 1080p25 master really
    /// runs at. Being wrong here should mean refusing an ingest that would have just fitted,
    /// never accepting one that fills the disk.
    /// </summary>
    public static long EstimateBytes(long frames, int width, int height, int frameRate, int proxyKbps)
    {
        if (frames <= 0 || width <= 0 || height <= 0) return 0;

        double masterBytes = frames * (width * (double)height * 2.6 / 8.0);

        // The proxy is half raster; at "auto" the encoder settles around 5 Mbps for HD.
        double proxyBitsPerSecond = (proxyKbps > 0 ? proxyKbps : 5000) * 1000.0;
        double seconds = frames / (double)Math.Max(1, frameRate);

        return (long)(masterBytes + proxyBitsPerSecond * seconds / 8.0);
    }

    /// <summary>The same estimate for a job, at the 1080-line raster Emerald records.</summary>
    public static long EstimateBytes(long frames, int frameRate, RecordingProfile profile) =>
        EstimateBytes(frames, 1920, 1080, frameRate, profile.ProxyBitrateKbps);

    public static string Describe(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):F1} GB"
        : $"{bytes / (double)(1L << 20):F0} MB";
}
