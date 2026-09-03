using Emerald.Core;
using Emerald.Video;
using System.IO;
namespace Emerald.Media;

public sealed record MediaSelection(string Path, string Kind, IReadOnlyList<string> Files)
{
    public bool IsEmpty => Files.Count == 0;

    public string Summary => Kind switch
    {
        "file" => System.IO.Path.GetFileName(Path),
        _ when Files.Count == 0 => "no playable media found in this folder",
        _ => $"{Files.Count} file{(Files.Count == 1 ? "" : "s")}",
    };
}

public static class MediaScanner
{
    /// <summary>Container extensions a video server is plausibly asked to play out.</summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mxf", ".mov", ".mp4", ".m4v", ".avi", ".mkv", ".ts", ".m2t", ".m2ts", ".mts",
        ".mpg", ".mpeg", ".dv", ".gxf", ".lxf", ".webm", ".yuv", ".raw", ".wav",
    };

    /// <summary>
    /// Extensions accepted for an audio track. The video list is folded in deliberately: a
    /// .mov or .mxf is a perfectly good language bed, since only its audio is decoded.
    /// </summary>
    private static readonly HashSet<string> AudioExtensions =
        new(VideoExtensions, StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".mp3", ".aac", ".m4a", ".flac", ".ogg", ".opus", ".ac3", ".eac3",
            ".mp2", ".aif", ".aiff", ".wma",
        };

    public static bool IsPlayable(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Resolves a dropped or browsed path into an ordered video playlist. Folders are scanned
    /// one level deep and sorted the way the shell would sort them.
    /// </summary>
    public static MediaSelection? Resolve(string? path) => Resolve(path, VideoExtensions);

    /// <summary>Same, but for an audio track, where audio-only containers are also valid.</summary>
    public static MediaSelection? ResolveAudio(string? path) => Resolve(path, AudioExtensions);

    private static MediaSelection? Resolve(string? path, HashSet<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim().Trim('"');

        // A directly chosen file is taken at its word; the extension list only gates the
        // scanning of folders.
        if (File.Exists(path))
            return new MediaSelection(path, "file", new[] { Path.GetFileName(path) });

        if (!Directory.Exists(path)) return null;

        var files = Directory.EnumerateFiles(path)
            .Where(f => extensions.Contains(Path.GetExtension(f)))
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MediaSelection(path, "folder", files);
    }
}
