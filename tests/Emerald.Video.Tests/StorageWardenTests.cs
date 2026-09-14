using System.IO;
using Emerald.Media;
using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// The rolling window over the capture store.
///
/// This is the only thing in Emerald that deletes an operator's recordings, so what it will
/// not do matters as much as what it will: not without a limit, not the newest, not the file
/// the recorder still has open, and not down to nothing however small the limit is.
/// </summary>
public sealed class StorageWardenTests : IDisposable
{
    private readonly string _root;
    private readonly DateTime _now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    public StorageWardenTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "emerald-store-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(RecordingProfile.FolderFor(RecordingProfile.LowRes, _root));
        Directory.CreateDirectory(RecordingProfile.FolderFor(RecordingProfile.HighRes, _root));
    }

    /// <summary>Writes a segment pair of a given size, aged by minutes into the past.</summary>
    private void Segment(string stamp, int megabytes, int minutesOld)
    {
        DateTime written = _now.AddMinutes(-minutesOld);

        Write(RecordingProfile.LowRes, $"capture_{stamp}.mp4", megabytes / 10 + 1, written);
        Write(RecordingProfile.HighRes, $"capture_{stamp}.mov", megabytes, written);
    }

    private void Write(RecordingOutput output, string name, int megabytes, DateTime written)
    {
        string path = Path.Combine(RecordingProfile.FolderFor(output, _root), name);

        File.WriteAllBytes(path, new byte[megabytes * 1024 * 1024]);
        File.SetLastWriteTimeUtc(path, written);
    }

    private static long Mb(int megabytes) => megabytes * 1024L * 1024L;

    private int Remaining() =>
        Directory.GetFiles(RecordingProfile.FolderFor(RecordingProfile.HighRes, _root)).Length;

    [Fact]
    public void Without_a_limit_nothing_is_ever_deleted()
    {
        for (int i = 0; i < 6; i++) Segment($"2026-09-14_10-0{i}-00", 10, 60 - i);

        StorageSweep sweep = StorageWarden.Enforce(_root, limitBytes: 0, _now);

        Assert.False(sweep.DidAnything);
        Assert.Equal(6, Remaining());
    }

    [Fact]
    public void A_store_already_under_the_limit_is_left_alone()
    {
        for (int i = 0; i < 4; i++) Segment($"2026-09-14_10-0{i}-00", 10, 60 - i);

        Assert.False(StorageWarden.Enforce(_root, Mb(500), _now).DidAnything);
        Assert.Equal(4, Remaining());
    }

    [Fact]
    public void The_oldest_go_first_and_the_newest_are_kept()
    {
        // Six pairs, oldest first. Roughly 66 MB all told; a 40 MB limit should take the
        // first two or three and stop.
        for (int i = 0; i < 6; i++) Segment($"2026-09-14_10-0{i}-00", 10, 60 - i * 5);

        StorageSweep sweep = StorageWarden.Enforce(_root, Mb(40), _now);

        Assert.True(sweep.DidAnything);
        Assert.True(sweep.AfterBytes <= Mb(40), $"still {sweep.AfterBytes} bytes");

        string high = RecordingProfile.FolderFor(RecordingProfile.HighRes, _root);

        // The oldest is gone and the newest is not.
        Assert.False(File.Exists(Path.Combine(high, "capture_2026-09-14_10-00-00.mov")));
        Assert.True(File.Exists(Path.Combine(high, "capture_2026-09-14_10-05-00.mov")));
    }

    [Fact]
    public void Both_halves_of_a_recording_go_together()
    {
        for (int i = 0; i < 6; i++) Segment($"2026-09-14_10-0{i}-00", 10, 60 - i * 5);

        StorageWarden.Enforce(_root, Mb(40), _now);

        // A proxy without its master is a clip that cannot be edited; a master without its
        // proxy is one the strip will not list. Neither is a state to leave the store in.
        int proxies = Directory.GetFiles(RecordingProfile.FolderFor(RecordingProfile.LowRes, _root)).Length;

        Assert.Equal(Remaining(), proxies);
    }

    [Fact]
    public void The_segment_being_recorded_is_never_deleted()
    {
        // One old, one written seconds ago - which is what the open segment looks like.
        Segment("2026-09-14_09-00-00", 10, 120);
        Segment("2026-09-14_11-59-00", 10, 0);

        StorageWarden.Enforce(_root, Mb(1), _now);

        string high = RecordingProfile.FolderFor(RecordingProfile.HighRes, _root);

        Assert.True(File.Exists(Path.Combine(high, "capture_2026-09-14_11-59-00.mov")),
                    "the file the recorder still has open must survive");
    }

    [Fact]
    public void A_limit_far_too_small_still_leaves_the_recent_material()
    {
        for (int i = 0; i < 6; i++) Segment($"2026-09-14_10-0{i}-00", 10, 60 - i * 5);

        StorageWarden.Enforce(_root, Mb(1), _now);

        // A limit below one segment would otherwise empty the store and keep emptying it,
        // which is not a rolling window - it is a recording that goes nowhere.
        Assert.Equal(StorageWarden.AlwaysKeep, Remaining());
    }

    [Fact]
    public void The_size_of_the_store_is_both_halves_together()
    {
        Segment("2026-09-14_10-00-00", 10, 60);

        long size = StorageWarden.Size(_root);

        // 10 MB master plus a 2 MB proxy, give or take the proxy's rounding.
        Assert.True(size > Mb(10), $"only counted {size} bytes");
    }

    [Fact]
    public void A_store_that_is_not_there_is_zero_rather_than_a_failure()
    {
        Assert.Equal(0, StorageWarden.Size(Path.Combine(_root, "nowhere")));
        Assert.False(StorageWarden.Enforce(Path.Combine(_root, "nowhere"), Mb(1), _now).DidAnything);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
