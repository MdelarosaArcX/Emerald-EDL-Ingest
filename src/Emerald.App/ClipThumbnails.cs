using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace Emerald.App;

/// <summary>
/// A frame out of each recording, for the clip strip under the stage.
///
/// The frame is pulled with ffmpeg — the same one the recorder writes with — and cached on
/// disk under the user's temp folder, keyed by the file's path and write time. A store of
/// two-minute segments is long-lived and the strip is rebuilt on every rescan, so decoding
/// a frame per clip per look would cost far more than keeping the PNGs around.
///
/// Everything here fails quietly: a missing ffmpeg, a segment still being written, a clip in
/// a container ffmpeg cannot seek — all of them mean no picture, which the strip already has
/// a placeholder for.
/// </summary>
public static class ClipThumbnails
{
    private const int Width = 294;

    private static readonly string CacheFolder =
        Path.Combine(Path.GetTempPath(), "Emerald", "thumbs");

    /// <summary>
    /// The cached frame for a clip, rendering it first if need be. Returns null when no
    /// picture could be produced.
    /// </summary>
    public static BitmapImage? Get(string ffmpegPath, string clipPath)
    {
        try
        {
            var file = new FileInfo(clipPath);
            if (!file.Exists || file.Length == 0) return null;

            string key = $"{clipPath.ToLowerInvariant()}|{file.LastWriteTimeUtc.Ticks}|{file.Length}";
            string cached = Path.Combine(CacheFolder, $"{Hash(key)}.png");

            if (!File.Exists(cached) && !Render(ffmpegPath, clipPath, cached)) return null;

            return Load(cached);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Render(string ffmpegPath, string clipPath, string destination)
    {
        if (!File.Exists(ffmpegPath)) return false;

        Directory.CreateDirectory(CacheFolder);

        var info = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // A second in, so a segment that opens on black does not cache as a black tile.
        // -ss before -i keeps it a seek rather than a decode of everything up to that point.
        foreach (string a in new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-ss", "1", "-i", clipPath, "-frames:v", "1",
            "-vf", $"scale={Width}:-1", destination,
        }) info.ArgumentList.Add(a);

        try
        {
            using Process? render = Process.Start(info);
            if (render is null) return false;

            render.StandardError.ReadToEnd();
            if (!render.WaitForExit(10000)) { try { render.Kill(true); } catch { } return false; }

            return File.Exists(destination) && new FileInfo(destination).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Loaded eagerly and frozen: the strip binds these from the UI thread while they are
    /// produced on another, and a lazily-loaded bitmap would also keep the cache file open.
    /// </summary>
    private static BitmapImage Load(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string Hash(string key)
    {
        byte[] digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(digest, 0, 10);
    }
}
