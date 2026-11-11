using Emerald.Media;
using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// The store kept listed: masters placed on the clock by their own lengths rather than paired by
/// number, and a refresh that opens only what has changed.
///
/// Against files ffmpeg actually wrote, laid out the way the recorder lays them out, with the
/// master cut longer than the proxy as it now is. Skipped quietly on a machine without ffmpeg.
/// </summary>
public sealed class StoreIndexTests : IDisposable
{
    private static string? Ffmpeg => Emerald.Video.Ffmpeg.Locate(null);
    private static string? Ffprobe => Ffmpeg is { } f ? MediaProbe.LocateFfprobe(f) : null;

    private const string Recording = "2026-11-10_09-00-00";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "emerald-storeindex", Guid.NewGuid().ToString("N"));
    private readonly string _low;
    private readonly string _high;

    public StoreIndexTests()
    {
        _low = Path.Combine(RecordingProfile.FolderFor(RecordingProfile.LowRes, _root), Recording);
        _high = Path.Combine(RecordingProfile.FolderFor(RecordingProfile.HighRes, _root), Recording);
        Directory.CreateDirectory(_low);
        Directory.CreateDirectory(_high);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly Dictionary<(double, string), byte[]> Made = new();

    /// <summary>A real, closed file of the given length; made once per length and copied after.</summary>
    private static void Encode(string path, double seconds)
    {
        string extension = Path.GetExtension(path);

        lock (Made)
        {
            if (!Made.TryGetValue((seconds, extension), out byte[]? bytes))
            {
                string temp = Path.Combine(Path.GetTempPath(), $"emerald-storeindex-{Guid.NewGuid():N}{extension}");

                var psi = new System.Diagnostics.ProcessStartInfo(Ffmpeg!)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                };
                foreach (string a in new[]
                         {
                             "-hide_banner", "-loglevel", "error", "-y",
                             "-f", "lavfi", "-i", $"testsrc=size=64x48:rate=25:duration={seconds:0.###}",
                             "-c:v", "libx264", "-preset", "ultrafast", "-g", "1", "-pix_fmt", "yuv420p", temp,
                         })
                    psi.ArgumentList.Add(a);

                using (var p = System.Diagnostics.Process.Start(psi)!) p.WaitForExit();

                bytes = File.ReadAllBytes(temp);
                File.Delete(temp);
                Made[(seconds, extension)] = bytes;
            }

            File.WriteAllBytes(path, bytes);
        }
    }

    private string Proxy(int n) => Path.Combine(_low, $"media_1110260900_{n}.mp4");
    private string Master(int n) => Path.Combine(_high, $"media_1110260900_{n}.mov");

    /// <summary>What a file looks like while the recorder still has it open: no movie header.</summary>
    private static void Open(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
    }

    private static CapturedClip Clip(StoreIndex index, string path) =>
        index.Clips.Single(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Each_proxy_is_given_the_masters_its_time_falls_in()
    {
        if (Ffmpeg is null) return;

        // Proxies of 2 s and masters of 3 s: proxy 2 runs 2-4 s, across the masters' boundary at 3.
        for (int n = 1; n <= 3; n++) Encode(Proxy(n), 2);
        for (int n = 1; n <= 2; n++) Encode(Master(n), 3);

        var index = new StoreIndex(_root, Ffprobe);
        index.Refresh(full: true);

        Assert.Equal(new[] { Master(1) }, Clip(index, Proxy(1)).Masters!.Select(m => m.Path));
        Assert.Equal(new[] { Master(1), Master(2) }, Clip(index, Proxy(2)).Masters!.Select(m => m.Path));
        Assert.Equal(new[] { Master(2) }, Clip(index, Proxy(3)).Masters!.Select(m => m.Path));

        IReadOnlyList<MasterSegment> masters = index.MastersOf(Recording);
        DateTime rolled = new(2026, 11, 10, 9, 0, 0);

        Assert.Equal(rolled, masters[0].Starts);
        Assert.Equal(rolled.AddSeconds(3), masters[1].Starts);
        Assert.Equal(rolled.AddSeconds(4), Clip(index, Proxy(3)).Starts);
        Assert.All(index.Clips, c => Assert.True(c.HasMaster));
    }

    [Fact]
    public void A_proxy_whose_master_is_still_being_written_has_no_master_to_cut_from_yet()
    {
        if (Ffmpeg is null) return;

        for (int n = 1; n <= 4; n++) Encode(Proxy(n), 1);
        Encode(Master(1), 2);
        Encode(Master(2), 2);
        Open(Master(2));

        var index = new StoreIndex(_root, Ffprobe);
        index.Refresh(full: true);

        Assert.True(Clip(index, Proxy(2)).HasMaster);
        Assert.False(Clip(index, Proxy(3)).HasMaster);
        Assert.True(Clip(index, Proxy(3)).MasterStillBeingWritten);
        Assert.Equal("master in-progress", Clip(index, Proxy(4)).MasterText);
        Assert.True(index.MastersOf(Recording)[1].Open);

        // And once the recorder closes it.
        Encode(Master(2), 2);

        Assert.True(index.Refresh());
        Assert.True(Clip(index, Proxy(4)).HasMaster);
    }

    [Fact]
    public void A_recording_cut_alike_still_pairs_one_master_to_each_proxy()
    {
        if (Ffmpeg is null) return;

        // How every recording was written before the master got its own length.
        for (int n = 1; n <= 3; n++)
        {
            Encode(Proxy(n), 2);
            Encode(Master(n), 2);
        }

        var index = new StoreIndex(_root, Ffprobe);
        index.Refresh(full: true);

        for (int n = 1; n <= 3; n++)
        {
            CapturedClip clip = Clip(index, Proxy(n));
            Assert.Equal(Master(n), clip.MasterPath);
            Assert.Equal(Master(n), Assert.Single(clip.Masters!).Path);
        }
    }

    [Fact]
    public void A_refresh_with_nothing_new_opens_nothing_and_a_new_file_opens_one()
    {
        if (Ffmpeg is null) return;

        for (int n = 1; n <= 5; n++) Encode(Proxy(n), 1);
        Encode(Master(1), 5);

        var index = new StoreIndex(_root, Ffprobe);
        index.Refresh(full: true);

        int read = index.FilesRead;
        Assert.Equal(6, read);

        // A full pass too: walking a directory is not reading its files.
        Assert.False(index.Refresh(full: true));
        Assert.Equal(read, index.FilesRead);

        Encode(Proxy(6), 1);

        Assert.True(index.Refresh());
        Assert.Equal(read + 1, index.FilesRead);
        Assert.Equal(6, index.Clips.Count);
    }

    [Fact]
    public void A_file_the_storage_limit_deleted_leaves_the_listing()
    {
        if (Ffmpeg is null) return;

        for (int n = 1; n <= 3; n++) Encode(Proxy(n), 1);

        var index = new StoreIndex(_root, Ffprobe);
        index.Refresh(full: true);

        IReadOnlyList<CapturedClip> before = index.Clips;
        File.Delete(Proxy(1));

        Assert.True(index.Refresh(full: true));
        Assert.DoesNotContain(index.Clips, c => c.Path == Proxy(1));

        // The list handed out before is a list of its own, not changed under whoever holds it.
        Assert.Equal(3, before.Count);
    }

    [Fact]
    public void The_storage_limit_takes_a_master_with_the_proxies_it_holds_and_notes_each_ones_own_length()
    {
        if (Ffmpeg is null) return;

        // Four masters of 3 s, twelve proxies of 1 s: three proxies to a master. Four groups, so
        // the floor of three leaves room to take exactly the oldest one.
        DateTime now = DateTime.UtcNow;

        for (int n = 1; n <= 12; n++)
        {
            Encode(Proxy(n), 1);
            File.SetLastWriteTimeUtc(Proxy(n), now.AddMinutes(-60 + n));
        }

        for (int n = 1; n <= 4; n++)
        {
            Encode(Master(n), 3);
            File.SetLastWriteTimeUtc(Master(n), now.AddMinutes(-60 + n * 3));
        }

        StorageSweep sweep = StorageWarden.Enforce(_root, StorageWarden.Size(_root) - 1, now);

        Assert.Equal(1, sweep.Deleted);
        Assert.False(File.Exists(Master(1)));
        for (int n = 1; n <= 3; n++) Assert.False(File.Exists(Proxy(n)), $"proxy {n} should have gone with master 1");
        Assert.True(File.Exists(Proxy(4)));
        Assert.True(File.Exists(Master(2)));

        // Each folder's ledger in its own lengths, so what is left is still placed where it was.
        Assert.Equal(new PruneLedger.Entry(3, TimeSpan.FromSeconds(3)), PruneLedger.Read(_low));
        Assert.Equal(new PruneLedger.Entry(1, TimeSpan.FromSeconds(3)), PruneLedger.Read(_high));

        var index = new StoreIndex(_root, Ffprobe);
        index.Refresh(full: true);

        DateTime rolled = new(2026, 11, 10, 9, 0, 0);
        Assert.Equal(rolled.AddSeconds(3), Clip(index, Proxy(4)).Starts);
        Assert.Equal(rolled.AddSeconds(3), index.MastersOf(Recording)[0].Starts);
        Assert.Equal(Master(2), Assert.Single(Clip(index, Proxy(4)).Masters!).Path);
    }

    [Fact]
    public void A_proxy_still_growing_is_not_a_change()
    {
        if (Ffmpeg is null) return;

        Encode(Proxy(1), 1);
        Encode(Proxy(2), 1);
        Open(Proxy(2));

        var index = new StoreIndex(_root, Ffprobe);
        index.Refresh(full: true);

        Assert.True(Clip(index, Proxy(2)).StillBeingWritten);

        // The recorder writes a little more of it.
        File.AppendAllText(Proxy(2), new string('x', 4096));

        Assert.False(index.Refresh());
    }
}
