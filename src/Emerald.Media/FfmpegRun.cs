using System.Diagnostics;
using System.Globalization;
using Emerald.Core;

namespace Emerald.Media;

/// <summary>What came of one ffmpeg run.</summary>
public readonly record struct FfmpegOutcome(bool Ok, bool Cancelled, string Problem);

/// <summary>
/// Runs ffmpeg for a helper job - an export cut, a low-res copy - and reports how far through it
/// is, so a progress bar can show a real fraction rather than spinning.
///
/// <b>How far.</b> ffmpeg is asked for <c>-progress pipe:1</c>, which writes key=value lines to
/// standard output twice a second; <c>out_time_us</c> against the length of what is being written
/// is the fraction. Standard error is read on its own task at the same time, because a process
/// whose error pipe fills while nobody reads it stops dead.
/// </summary>
public static class FfmpegRun
{
    /// <summary>
    /// Runs ffmpeg with <paramref name="arguments"/> - which must not already ask for progress -
    /// over <paramref name="seconds"/> of media, telling <paramref name="fraction"/> from 0 to 1.
    /// Stopped and reported as cancelled when <paramref name="cancel"/> fires.
    /// </summary>
    public static FfmpegOutcome Run(string ffmpegPath, IEnumerable<string> arguments, double seconds,
                                    Action<double>? fraction, CancellationToken cancel,
                                    int ioPriority = BackgroundPriority.LowIo)
    {
        var info = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        info.ArgumentList.Add("-progress");
        info.ArgumentList.Add("pipe:1");
        info.ArgumentList.Add("-nostats");
        foreach (string a in arguments) info.ArgumentList.Add(a);

        Process? ffmpeg;

        try { ffmpeg = Process.Start(info); }
        catch (Exception ex) { return new FfmpegOutcome(false, false, $"ffmpeg could not be started: {ex.Message}"); }

        if (ffmpeg is null) return new FfmpegOutcome(false, false, "ffmpeg could not be started.");

        using (ffmpeg)
        {
            BackgroundPriority.Apply(ffmpeg, ioPriority);

            Task<string> errors = ffmpeg.StandardError.ReadToEndAsync();

            using (cancel.Register(() => Stop(ffmpeg)))
            {
                string? line;

                while ((line = ffmpeg.StandardOutput.ReadLine()) is not null)
                {
                    if (fraction is null || seconds <= 0) continue;

                    // out_time_us is microseconds; "N/A" before the first frame is out.
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                        long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us))
                        fraction(Math.Clamp(us / 1_000_000.0 / seconds, 0, 1));
                    else if (line == "progress=end")
                        fraction(1);
                }

                ffmpeg.WaitForExit();
            }

            string complaints = errors.Wait(TimeSpan.FromSeconds(5)) ? errors.Result.Trim() : "";

            if (cancel.IsCancellationRequested) return new FfmpegOutcome(false, true, "Called off.");

            return ffmpeg.ExitCode == 0
                ? new FfmpegOutcome(true, false, "")
                : new FfmpegOutcome(false, false, complaints.Length > 0 ? complaints : $"ffmpeg exited {ffmpeg.ExitCode}.");
        }
    }

    private static void Stop(Process ffmpeg)
    {
        try { if (!ffmpeg.HasExited) ffmpeg.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>A time for ffmpeg, invariant so a comma decimal separator cannot turn 12.5 into 12.</summary>
    public static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
