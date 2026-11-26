using System.Diagnostics;
using Emerald.Media;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// The ffmpeg runner behind exports and low-res copies: progress is reported as a fraction, a
/// cancel stops it, and running it over and over leaves no handles behind - Live Edit makes a
/// low-res copy for every file dropped in, for as long as it is open.
///
/// Like the export tests, these return without checking anything on a machine with no ffmpeg.
/// </summary>
[Collection(ProcessWideCollection.Name)]
public sealed class FfmpegRunTests
{
    private static readonly string[] OneSecond =
    {
        "-hide_banner", "-loglevel", "error", "-nostdin",
        "-f", "lavfi", "-i", "testsrc=s=64x36:r=25:d=1", "-f", "null", "-",
    };

    [Fact]
    public void Progress_runs_from_nothing_to_done()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg) return;

        var fractions = new List<double>();
        FfmpegOutcome outcome = FfmpegRun.Run(ffmpeg, OneSecond, 1, fractions.Add, CancellationToken.None);

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.NotEmpty(fractions);
        Assert.Equal(1, fractions[^1]);
        Assert.All(fractions, f => Assert.InRange(f, 0, 1));
    }

    [Fact]
    public void A_cancel_stops_it_and_says_so()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg) return;

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        string[] long_ = { "-hide_banner", "-loglevel", "error", "-nostdin", "-re",
                           "-f", "lavfi", "-i", "testsrc=s=64x36:r=25:d=60", "-f", "null", "-" };

        var took = Stopwatch.StartNew();
        FfmpegOutcome outcome = FfmpegRun.Run(ffmpeg, long_, 60, null, cancel.Token);

        Assert.True(outcome.Cancelled);
        Assert.False(outcome.Ok);
        Assert.True(took.Elapsed < TimeSpan.FromSeconds(20), $"took {took.Elapsed} to stop");
    }

    [Fact]
    public void A_bad_input_is_a_failure_with_ffmpegs_own_words()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg) return;

        FfmpegOutcome outcome = FfmpegRun.Run(ffmpeg,
            new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", "no-such-file.mov", "-f", "null", "-" },
            1, null, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.False(outcome.Cancelled);
        Assert.Contains("no-such-file", outcome.Problem);
    }

    [Fact]
    public void Running_it_again_and_again_leaves_no_handles_behind()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg) return;

        FfmpegRun.Run(ffmpeg, OneSecond, 1, _ => { }, CancellationToken.None);

        using Process me = Process.GetCurrentProcess();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        me.Refresh();
        int before = me.HandleCount;

        for (int i = 0; i < 25; i++)
            Assert.True(FfmpegRun.Run(ffmpeg, OneSecond, 1, _ => { }, CancellationToken.None).Ok);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        me.Refresh();

        // Each run is a process, its thread and three pipes: a leak would be over a hundred.
        Assert.True(me.HandleCount - before < 25, $"{before} handles before, {me.HandleCount} after 25 runs");
    }
}
