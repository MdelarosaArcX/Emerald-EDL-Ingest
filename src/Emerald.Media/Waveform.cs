using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Emerald.Core;

namespace Emerald.Media;

/// <summary>
/// A file's waveform at two resolutions: <see cref="Fine"/>, a peak for every fiftieth of a
/// second, and <see cref="Coarse"/>, a peak for every second. The coarse one is for a timeline
/// zoomed out to hours, where a pixel covers minutes and reading every fine peak under it on
/// every redraw would be millions of reads.
/// </summary>
public sealed class WavePeaks
{
    public WavePeaks(float[] fine)
    {
        Fine = fine;
        Coarse = new float[(fine.Length + Waveform.PeaksPerSecond - 1) / Waveform.PeaksPerSecond];

        for (int i = 0; i < fine.Length; i++)
        {
            int c = i / Waveform.PeaksPerSecond;
            if (fine[i] > Coarse[c]) Coarse[c] = fine[i];
        }
    }

    public float[] Fine { get; }
    public float[] Coarse { get; }

    /// <summary>The loudest peak between two points in seconds, from whichever resolution is enough.</summary>
    public float Max(double fromSeconds, double toSeconds)
    {
        bool coarse = toSeconds - fromSeconds > 2;
        float[] peaks = coarse ? Coarse : Fine;
        int per = coarse ? 1 : Waveform.PeaksPerSecond;

        int a = Math.Max(0, (int)Math.Floor(fromSeconds * per));
        int b = Math.Min(peaks.Length - 1, (int)Math.Ceiling(toSeconds * per));

        float max = 0;
        for (int i = a; i <= b; i++)
            if (peaks[i] > max) max = peaks[i];

        return max;
    }
}

/// <summary>
/// The loudness of a file's sound over time, for drawing under its block on a timeline: the
/// peak of each fiftieth of a second, from 0 (silence) to 1 (full scale).
///
/// Read with ffmpeg, mixed to mono at a low rate - a picture of the sound needs nothing like the
/// whole of it, and a ten-second segment is then read in a fraction of a second.
/// </summary>
public static class Waveform
{
    /// <summary>How many peaks a second of sound becomes. Two to a frame at 25 fps.</summary>
    public const int PeaksPerSecond = 50;

    private const int SampleRate = 8000;
    private const int SamplesPerPeak = SampleRate / PeaksPerSecond;

    /// <summary>
    /// Waveforms already read, of files that have stopped changing. Keyed on length and write
    /// time as well as path, as the probe cache is, so a file replaced under the same name is
    /// read again.
    /// </summary>
    private static readonly ConcurrentDictionary<string, WavePeaks> Read = new();

    /// <summary>Beyond this the cache is started again: files pruned from the store leave entries behind.</summary>
    private const int ReadMost = 5_000;

    /// <summary>How many files' waveforms are remembered, for the memory monitor.</summary>
    public static int Remembered => Read.Count;

    /// <summary>
    /// Where read waveforms are kept on disk, or null to keep them in memory only. Set by the
    /// application to a folder on the database's drive.
    ///
    /// A waveform is read by decoding the whole file's sound, which reads the whole file. Kept
    /// here, a segment's sound is read from the recording disk once, ever - not again each time
    /// Live Edit opens, scrolls back over it, or starts the next day. A 30-second segment's
    /// waveform is 1.5 KB.
    /// </summary>
    public static string? CacheFolder { get; set; }

    /// <summary>Waveforms read from the disk cache rather than from the media, for the tests.</summary>
    public static int FromCache => _fromCache;
    private static int _fromCache;

    /// <summary>Files decoded with ffmpeg to read their waveform, for the tests.</summary>
    public static int Decoded => _decoded;
    private static int _decoded;

    /// <summary>Forgets what is held in memory, so a test can see what the disk cache holds.</summary>
    internal static void ForgetRemembered() => Read.Clear();

    /// <summary>Deletes cached waveforms not used for <paramref name="days"/> days. Returns how many went.</summary>
    public static int SweepCache(int days)
    {
        if (CacheFolder is not { } folder || days < 0 || !Directory.Exists(folder)) return 0;

        DateTime cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, days));
        int swept = 0;

        try
        {
            foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("*.peaks"))
            {
                if (file.LastWriteTimeUtc >= cutoff) continue;
                try { file.Delete(); swept++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return swept;
    }

    /// <summary>The waveform if it has been read already, without reading it.</summary>
    public static WavePeaks? Known(string path) =>
        Key(path) is { } key && Read.TryGetValue(key, out WavePeaks? peaks) ? peaks : null;

    /// <summary>
    /// The peaks of a file's sound, read with ffmpeg on first asking. An empty array for a file
    /// with no sound; null when it cannot be read at all.
    /// </summary>
    public static WavePeaks? Peaks(string ffmpegPath, string path)
    {
        if (Key(path) is not { } key) return null;

        if (Read.TryGetValue(key, out WavePeaks? known)) return known;

        WavePeaks? peaks = Cached(key);

        if (peaks is not null)
        {
            Interlocked.Increment(ref _fromCache);
        }
        else if (ReadPeaks(ffmpegPath, path) is { } fine)
        {
            Interlocked.Increment(ref _decoded);
            peaks = new WavePeaks(fine);

            // Nothing read is not kept: it may be a read that failed, and kept on disk it would
            // never be tried again.
            if (fine.Length > 0) Keep(key, fine);
        }

        if (peaks is not null)
        {
            if (Read.Count >= ReadMost) Read.Clear();
            Read[key] = peaks;
        }

        return peaks;
    }

    /// <summary>The cache file for a file as it is now: its path, length and write time, hashed.</summary>
    private static string? CachePath(string key)
    {
        if (CacheFolder is not { } folder) return null;

        byte[] hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key.ToUpperInvariant()));
        return Path.Combine(folder, Convert.ToHexString(hash) + ".peaks");
    }

    /// <summary>
    /// A waveform from the disk cache. Stored a byte a peak: a picture of the sound a few pixels
    /// high needs nothing finer, and it is a quarter of the size.
    /// </summary>
    private static WavePeaks? Cached(string key)
    {
        if (CachePath(key) is not { } path) return null;

        try
        {
            if (!File.Exists(path)) return null;

            byte[] bytes = File.ReadAllBytes(path);
            var peaks = new float[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) peaks[i] = bytes[i] / 255f;

            // Touched, so the sweep keeps what is still being looked at.
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (IOException) { }

            return new WavePeaks(peaks);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void Keep(string key, float[] peaks)
    {
        if (CachePath(key) is not { } path) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var bytes = new byte[peaks.Length];
            for (int i = 0; i < peaks.Length; i++)
                bytes[i] = (byte)Math.Clamp((int)Math.Ceiling(peaks[i] * 255), 0, 255);

            // Whole or not at all, so a reader never takes half a file for a short sound.
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string? Key(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}" : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static float[]? ReadPeaks(string ffmpegPath, string path)
    {
        try
        {
            var info = new ProcessStartInfo(ffmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (string arg in new[]
                     {
                         "-v", "error", "-nostdin", "-i", path, "-vn", "-ac", "1",
                         "-ar", SampleRate.ToString(), "-f", "s16le", "-acodec", "pcm_s16le", "-",
                     })
                info.ArgumentList.Add(arg);

            using Process? ffmpeg = Process.Start(info);
            if (ffmpeg is null) return null;

            // Behind the recorder for the disk: this reads a whole file to draw a picture.
            BackgroundPriority.Apply(ffmpeg);

            // Drained alongside, or a chatty file fills the error pipe and ffmpeg stops writing.
            _ = ffmpeg.StandardError.ReadToEndAsync();

            Stream pcm = ffmpeg.StandardOutput.BaseStream;
            var peaks = new List<float>(1024);
            var buffer = new byte[SamplesPerPeak * 2 * 64];
            int held = 0, inPeak = 0, peak = 0;

            while (true)
            {
                int read = pcm.Read(buffer, held, buffer.Length - held);
                if (read <= 0) break;

                int bytes = held + read;
                int whole = bytes & ~1;

                for (int o = 0; o < whole; o += 2)
                {
                    int sample = Math.Abs((int)(short)(buffer[o] | (buffer[o + 1] << 8)));
                    if (sample > peak) peak = sample;

                    if (++inPeak == SamplesPerPeak)
                    {
                        peaks.Add(peak / 32768f);
                        inPeak = 0;
                        peak = 0;
                    }
                }

                // An odd byte left over is the first half of the next sample.
                held = bytes - whole;
                if (held > 0) buffer[0] = buffer[whole];
            }

            if (inPeak > 0) peaks.Add(peak / 32768f);

            ffmpeg.WaitForExit(15000);

            return peaks.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
