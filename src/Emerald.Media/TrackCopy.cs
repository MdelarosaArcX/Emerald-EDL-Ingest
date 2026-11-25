using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Emerald.Core;

namespace Emerald.Media;

/// <summary>
/// A copy of a recorded segment with one chosen sound track in it, so a player that cannot
/// be told which of a file's tracks to play is given no choice.
///
/// Windows' media player plays one track of a file, and of several it is the last. The picture and the one track are copied out
/// as they are - nothing is decoded or encoded, so a thirty-second proxy takes a moment - and the
/// copy plays exactly as the segment does, frame for frame, with sound and picture still together.
///
/// Kept in a folder of its own, the most recent <see cref="MostKept"/> only: what is scrolled back to
/// is made again.
/// </summary>
public static class TrackCopy
{
    /// <summary>Where the copies are made, or null when there is nowhere. Set by the application.</summary>
    public static string? Folder { get; set; }

    /// <summary>How many are kept. Only the small previews play from these.</summary>
    public const int MostKept = 12;

    private static readonly Dictionary<string, Action> Making = new();
    private static readonly ConcurrentDictionary<string, bool> Failed = new();

    // One at a time, as waveforms are: each reads a file off the disk being recorded to.
    private static readonly SemaphoreSlim One = new(1);

    /// <summary>The copy of a file with this track in it, if it has been made.</summary>
    public static string? Known(string path, int track) =>
        PathFor(path, track) is { } copy && File.Exists(copy) ? copy : null;

    /// <summary>True when it was tried and could not be made.</summary>
    public static bool HasFailed(string path, int track) => Failed.ContainsKey($"{path}|{track}");

    /// <summary>
    /// Makes the copy in the background, once however often it is asked for meanwhile.
    /// <paramref name="done"/> is called, off the caller's thread, when it is there or cannot be.
    /// </summary>
    public static void Request(string ffmpegPath, string path, int track, Action done)
    {
        string key = $"{path}|{track}";

        if (Failed.ContainsKey(key)) return;

        lock (Making)
        {
            // Being made already: told with the rest, and once - a window asking every tick passes
            // the same thing each time.
            if (Making.TryGetValue(key, out Action? waiting))
            {
                if (!waiting.GetInvocationList().Any(d => d.Method == done.Method)) Making[key] = waiting + done;
                return;
            }

            Making[key] = done;
        }

        _ = Task.Run(async () =>
        {
            await One.WaitAsync();

            try
            {
                if (Make(ffmpegPath, path, track) is null) Failed[key] = true;
            }
            finally
            {
                One.Release();
            }

            Action told;

            lock (Making)
            {
                told = Making[key];
                Making.Remove(key);
            }

            told();
        });
    }

    /// <summary>Makes the copy and returns where it is, or null when it could not be made.</summary>
    public static string? Make(string ffmpegPath, string path, int track)
    {
        if (PathFor(path, track) is not { } copy) return null;
        if (File.Exists(copy)) return copy;

        // Its own name until it is whole, so a player is never handed half of one.
        string temp = Path.ChangeExtension(copy, ".part.mp4");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);

            var info = new ProcessStartInfo(ffmpegPath)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (string arg in new[]
                     {
                         "-v", "error", "-nostdin", "-y", "-i", path,
                         "-map", "0:v:0", "-map", $"0:a:{track}", "-c", "copy", temp,
                     })
                info.ArgumentList.Add(arg);

            using Process? ffmpeg = Process.Start(info);
            if (ffmpeg is null) return null;

            BackgroundPriority.Apply(ffmpeg);

            string problem = ffmpeg.StandardError.ReadToEnd();
            ffmpeg.WaitForExit(30000);

            if (!ffmpeg.HasExited || ffmpeg.ExitCode != 0)
            {
                ActivityLog.Shared.Warn(LogSource.App,
                    $"Audio {track + 1} of {Path.GetFileName(path)} could not be copied out to listen to: {problem.Trim()}",
                    @event: "liveedit.trackcopyfailed", file: path);
                return null;
            }

            File.Move(temp, copy, overwrite: true);
            Trim();
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                         InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ActivityLog.Shared.Warn(LogSource.App,
                $"Audio {track + 1} of {Path.GetFileName(path)} could not be copied out to listen to: {ex.Message}",
                @event: "liveedit.trackcopyfailed", file: path);
            return null;
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Lets go of the oldest copies. One a player still has open stays until next time.</summary>
    private static void Trim()
    {
        if (Folder is not { } folder) return;

        foreach (FileInfo old in new DirectoryInfo(folder).EnumerateFiles("*.mp4")
                     .OrderByDescending(f => f.LastWriteTimeUtc).Skip(MostKept).ToList())
        {
            try { old.Delete(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Keyed on length and write time as well, so a file replaced under the same name is copied again.</summary>
    private static string? PathFor(string path, int track)
    {
        if (Folder is not { } folder) return null;

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;

            string key = $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{track}".ToUpperInvariant();
            byte[] hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key));

            return Path.Combine(folder, Convert.ToHexString(hash) + ".mp4");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
