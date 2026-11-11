using System.IO;
using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>Working set is a figure for the whole process, so nothing else may run beside this.</summary>
[CollectionDefinition("Working set", DisableParallelization = true)]
public sealed class WorkingSetCollection { }

[Collection("Working set")]
public sealed class DelayLineWorkingSetTests : IDisposable
{
    private readonly string _folder;

    public DelayLineWorkingSetTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "emerald-delay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    /// <summary>
    /// The reason trimming exists. A ready line nothing reads holds its last ten seconds, not the
    /// ring: left alone, the working set here would rise by everything written.
    /// </summary>
    [Fact]
    public void A_line_nothing_reads_does_not_stay_in_the_working_set()
    {
        var format = new CaptureFormat("test360", 640, 360, 25, 0);

        using DelayLine line = DelayLine.Create(_folder, format, TimeSpan.FromSeconds(60));

        var frame = new byte[line.FrameBytes];
        long frames = line.SlotCount;
        long written = frames * DelayLine.SlotStrideFor(format);

        long before = Environment.WorkingSet;

        for (long i = 0; i < frames; i++)
            line.Write(frame, frame.Length, null, 0, false);

        long grew = Environment.WorkingSet - before;

        Assert.True(grew < written / 2, $"grew {grew >> 20} MB having written {written >> 20} MB");
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* a temp folder */ }
    }
}
