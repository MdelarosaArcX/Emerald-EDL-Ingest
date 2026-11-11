using System.Diagnostics;
using System.Text.Json;
using Emerald.Core;
using Emerald.Media;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// The developer's memory record: that it says true things about the process, names the ffmpegs
/// this process started and no others, and carries what the modules report about themselves.
/// </summary>
public sealed class MemoryMonitorTests
{
    [Fact]
    public void A_snapshot_describes_this_process()
    {
        MemorySnapshot now = MemoryMonitor.Take();

        Assert.True(now.PrivateBytes > 0);
        Assert.True(now.WorkingSetBytes > 0);
        Assert.True(now.ManagedHeapBytes > 0);
        Assert.True(now.ManagedHeapBytes < now.PrivateBytes);
        Assert.True(now.Threads > 0);
        Assert.True(now.AllocatedBytesTotal > 0);
    }

    [Fact]
    public void A_gauge_is_read_while_it_is_registered_and_not_after()
    {
        string name = "test " + Guid.NewGuid().ToString("N");

        using (MemoryGauges.Register("Test", name, () => 4242))
            Assert.Contains(MemoryMonitor.Take().Gauges, g => g.Name == name && g.Value == 4242 && g.Module == "Test");

        Assert.DoesNotContain(MemoryMonitor.Take().Gauges, g => g.Name == name);
    }

    [Fact]
    public void A_gauge_that_throws_is_left_out_and_takes_nothing_else_with_it()
    {
        string good = "test " + Guid.NewGuid().ToString("N");

        using IDisposable broken = MemoryGauges.Register("Test", "broken " + good, () => throw new ObjectDisposedException("gone"));
        using IDisposable fine = MemoryGauges.Register("Test", good, () => 7);

        Assert.Contains(MemoryMonitor.Take().Gauges, g => g.Name == good);
    }

    [Fact]
    public void An_ffmpeg_this_process_started_is_listed_with_what_it_is_for()
    {
        if (Emerald.Video.Ffmpeg.Locate(null) is not { } ffmpeg) return;

        var info = new ProcessStartInfo(ffmpeg, "-hide_banner -loglevel error -f lavfi -i anullsrc -t 20 -f null -")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process child = Process.Start(info)!;

        try
        {
            MemoryGauges.NameChild(child.Id, "test decoder");

            ChildProcessMemory? found = MemoryMonitor.Take().Children.FirstOrDefault(c => c.Id == child.Id);

            Assert.NotNull(found);
            Assert.Equal("test decoder", found!.Role);
            Assert.Equal("ffmpeg", found.Name);
            Assert.True(found.PrivateBytes > 0);
        }
        finally
        {
            child.Kill(true);
            child.WaitForExit();
        }

        Assert.DoesNotContain(MemoryMonitor.Take().Children, c => c.Id == child.Id);
    }

    [Fact]
    public void Taking_snapshots_for_days_does_not_use_up_handles()
    {
        // Every ten seconds for as long as the application runs: a handle left open by each one
        // would be eight thousand a day.
        MemoryMonitor.Take();

        using Process me = Process.GetCurrentProcess();
        int before = me.HandleCount;

        for (int i = 0; i < 400; i++) MemoryMonitor.Take();

        me.Refresh();

        Assert.True(me.HandleCount - before < 40, $"{before} handles before, {me.HandleCount} after 400 snapshots");
    }

    [Fact]
    public void The_record_is_one_line_of_json()
    {
        string line = MemoryMonitor.TakeJson();

        Assert.DoesNotContain('\n', line);

        using JsonDocument parsed = JsonDocument.Parse(line);

        Assert.True(parsed.RootElement.GetProperty("PrivateBytes").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Array, parsed.RootElement.GetProperty("Gauges").ValueKind);
    }

    [Fact]
    public void A_file_still_being_written_is_not_remembered_as_having_no_codec()
    {
        string path = Path.Combine(Path.GetTempPath(), "emerald-open-" + Guid.NewGuid().ToString("N") + ".mov");

        try
        {
            // No index yet, and a different length at every look: what a master being recorded is.
            // Each look used to leave an entry behind for good.
            int before = MediaCodec.Remembered;

            for (int look = 1; look <= 5; look++)
            {
                File.WriteAllBytes(path, new byte[64 * look]);
                Assert.Null(MediaCodec.ReadFourCc(path));
            }

            Assert.Equal(before, MediaCodec.Remembered);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
