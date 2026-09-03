using System.IO;

namespace Emerald.Video;

/// <summary>
/// Where ffmpeg lives. Every module that shells out to ffmpeg — playout, audio beds and
/// RX recording — resolves its path through here, so there is one answer per run rather
/// than three independent searches that could disagree.
/// </summary>
public static class Ffmpeg
{
    /// <summary>
    /// Finds ffmpeg: next to the application first, then PATH, then the usual manual
    /// install location. Returns null when it is not installed.
    /// </summary>
    public static string? Locate(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
        };

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(dir))
                candidates.Add(Path.Combine(dir.Trim(), "ffmpeg.exe"));
        }

        candidates.Add(@"C:\ffmpeg\bin\ffmpeg.exe");
        candidates.Add(@"C:\Program Files\ffmpeg\bin\ffmpeg.exe");

        return candidates.FirstOrDefault(File.Exists);
    }
}
