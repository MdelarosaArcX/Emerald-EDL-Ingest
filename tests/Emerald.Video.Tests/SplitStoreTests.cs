using Emerald.Core;
using Emerald.Media;
using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// The high-res and low-res folders chosen apart: the masters in one place, the proxies in
/// another, each with a storage limit of its own - and the readers that keep the recording disk
/// quiet (lengths read once for everyone, waveforms kept on disk once read).
///
/// Against files ffmpeg actually wrote where a test needs real media; skipped quietly on a machine
/// without ffmpeg.
/// </summary>
public sealed class SplitStoreTests : IDisposable
{
    private static string? Ffmpeg => Emerald.Video.Ffmpeg.Locate(null);
    private static string? Ffprobe => Ffmpeg is { } f ? MediaProbe.LocateFfprobe(f) : null;

    private const string Recording = "2026-11-10_09-00-00";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "emerald-split", Guid.NewGuid().ToString("N"));
    private readonly StorePaths _paths;
    private readonly string _low;
    private readonly string _high;

    public SplitStoreTests()
    {
        // Two unrelated folders, as two drives would be: neither is a high\ or low\ of a store.
        _paths = new StorePaths(Path.Combine(_temp, "masters"), Path.Combine(_temp, "proxies"));
        _low = Path.Combine(_paths.Low, Recording);
        _high = Path.Combine(_paths.High, Recording);
        Directory.CreateDirectory(_low);
        Directory.CreateDirectory(_high);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { }
    }

    private static readonly Dictionary<(double, string), byte[]> Made = new();

    private static void Encode(string path, double seconds)
    {
        string extension = Path.GetExtension(path);

        lock (Made)
        {
            if (!Made.TryGetValue((seconds, extension), out byte[]? bytes))
            {
                string temp = Path.Combine(Path.GetTempPath(), $"emerald-split-{Guid.NewGuid():N}{extension}");

                var psi = new System.Diagnostics.ProcessStartInfo(Ffmpeg!)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                };
                foreach (string a in new[]
                         {
                             "-hide_banner", "-loglevel", "error", "-y",
                             "-f", "lavfi", "-i", $"testsrc=size=64x48:rate=25:duration={seconds:0.###}",
                             "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds:0.###}",
                             "-c:v", "libx264", "-preset", "ultrafast", "-g", "1", "-pix_fmt", "yuv420p",
                             "-c:a", "aac", "-shortest", temp,
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

    // ------------------------------------------------------------------ the folders

    [Fact]
    public void A_store_as_it_always_was_is_high_and_low_under_one_folder()
    {
        StorePaths store = StorePaths.Under(@"D:\Emerald\media");

        Assert.Equal(@"D:\Emerald\media\high", store.High);
        Assert.Equal(@"D:\Emerald\media\low", store.Low);
        Assert.Equal(@"D:\Emerald\media", store.Root);
        Assert.Equal(@"D:\Emerald\media\ingest", store.Exports);
        Assert.Equal(RangeExport.FolderFor(@"D:\Emerald\media"), store.Exports);
        Assert.False(store.Split);
    }

    [Fact]
    public void Folders_chosen_apart_have_no_one_root_and_export_beside_the_masters()
    {
        var store = new StorePaths(@"E:\Masters", @"F:\Proxies");

        Assert.Null(store.Root);
        Assert.Equal(@"E:\Masters\ingest", store.Exports);
        Assert.Equal(@"E:\Masters", store.For(RecordingProfile.HighRes));
        Assert.Equal(@"F:\Proxies", store.For(RecordingProfile.LowRes));
        Assert.True(store.Split);
    }

    [Fact]
    public void A_folder_not_chosen_is_its_folder_in_the_store_it_always_was()
    {
        Assert.Equal(StorePaths.Under(@"D:\media"), MediaLibrary.PathsFor(@"D:\media", "", ""));
        Assert.Equal(new StorePaths(@"E:\Masters", @"D:\media\low"), MediaLibrary.PathsFor(@"D:\media", @"E:\Masters", ""));
        Assert.Equal(new StorePaths(@"E:\Masters", @"F:\Proxies"), MediaLibrary.PathsFor(@"D:\media", @"E:\Masters", @"F:\Proxies"));
    }

    [Fact]
    public void One_old_storage_limit_becomes_the_high_res_limit_and_a_chosen_one_is_kept()
    {
        AppSettings old = AppSettings.Migrate(new AppSettings { RecordingStorageLimitGb = 100 });
        Assert.Equal(100, old.HighResLimitGb);
        Assert.Equal(0, old.LowResLimitGb);

        AppSettings chosen = AppSettings.Migrate(new AppSettings { RecordingStorageLimitGb = 100, HighResLimitGb = 500, LowResLimitGb = 200 });
        Assert.Equal(500, chosen.HighResLimitGb);
        Assert.Equal(200, chosen.LowResLimitGb);
    }

    [Fact]
    public void Delay_ready_is_turned_on_once_and_a_later_off_is_kept()
    {
        AppSettings old = AppSettings.Migrate(new AppSettings { KeepDelayReady = false });
        Assert.True(old.KeepDelayReady);
        Assert.True(old.DelayReadyRevisited);

        old.KeepDelayReady = false;   // the operator turns it off again
        Assert.False(AppSettings.Migrate(old).KeepDelayReady);
    }

    [Fact]
    public void The_recorder_writes_each_output_to_its_own_folder()
    {
        var names = RecordingSequence.Next(new DateTime(2026, 11, 10, 9, 0, 0), "media");
        List<string> args = new RecordingProfile(0, 192, 48000, 30)
            .EncoderArguments(new CaptureFormat("1080p25", 1920, 1080, 25, 0), "pipe", @"C:\unused", "media", 48000,
                              names: names, store: new StorePaths(@"E:\Masters", @"F:\Proxies"))
            .ToList();

        Assert.Contains(args, a => a.StartsWith(@"F:\Proxies\" + names.Folder, StringComparison.OrdinalIgnoreCase) && a.EndsWith(".mp4"));
        Assert.Contains(args, a => a.StartsWith(@"E:\Masters\" + names.Folder, StringComparison.OrdinalIgnoreCase) && a.EndsWith(".mov"));
        Assert.DoesNotContain(args, a => a.StartsWith(@"C:\unused", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ reading a split store

    [Fact]
    public void The_store_index_finds_proxies_and_masters_in_their_own_folders()
    {
        if (Ffmpeg is null) return;

        for (int n = 1; n <= 3; n++) Encode(Proxy(n), 1);
        Encode(Master(1), 3);

        var index = new StoreIndex(_paths, Ffprobe);
        index.Refresh(full: true);

        Assert.Equal(3, index.Clips.Count);
        Assert.All(index.Clips, c => Assert.Equal(Master(1), Assert.Single(c.Masters!).Path));
        Assert.All(index.Clips, c => Assert.True(c.HasMaster));
    }

    // ------------------------------------------------------------------ limits of their own

    private void Groups(DateTime now)
    {
        // Four masters of 3 s, twelve proxies of 1 s, all long finished.
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
    }

    [Fact]
    public void Past_the_high_res_limit_the_oldest_master_goes_with_its_proxies()
    {
        if (Ffmpeg is null) return;

        DateTime now = DateTime.UtcNow;
        Groups(now);

        (long high, _) = StorageWarden.Size(_paths);
        (StorageSweep highSweep, StorageSweep lowSweep) = StorageWarden.Enforce(_paths, high - 1, 0, now);

        Assert.Equal(1, highSweep.Deleted);
        Assert.False(lowSweep.DidAnything);
        Assert.False(File.Exists(Master(1)));
        for (int n = 1; n <= 3; n++) Assert.False(File.Exists(Proxy(n)));
        Assert.True(File.Exists(Master(2)));
        Assert.True(File.Exists(Proxy(4)));
        Assert.True(highSweep.AfterBytes < high);
    }

    [Fact]
    public void Past_the_low_res_limit_only_proxies_go_and_their_masters_stay()
    {
        if (Ffmpeg is null) return;

        DateTime now = DateTime.UtcNow;
        Groups(now);

        (_, long low) = StorageWarden.Size(_paths);
        (StorageSweep highSweep, StorageSweep lowSweep) = StorageWarden.Enforce(_paths, 0, low - 1, now);

        Assert.False(highSweep.DidAnything);
        Assert.Equal(1, lowSweep.Deleted);
        Assert.False(File.Exists(Proxy(1)));
        Assert.True(File.Exists(Master(1)), "a low-res limit never takes a master");
        Assert.True(File.Exists(Proxy(4)));
        Assert.True(lowSweep.AfterBytes < low);
    }

    [Fact]
    public void Neither_limit_goes_below_the_newest_groups()
    {
        if (Ffmpeg is null) return;

        DateTime now = DateTime.UtcNow;
        Groups(now);

        StorageWarden.Enforce(_paths, 1, 1, now);

        // Four groups, three always kept: only the oldest group can go.
        for (int n = 2; n <= 4; n++) Assert.True(File.Exists(Master(n)));
        for (int n = 4; n <= 12; n++) Assert.True(File.Exists(Proxy(n)));
    }

    // ------------------------------------------------------------------ read once

    [Fact]
    public void A_finished_files_length_is_read_once_for_everyone()
    {
        if (Ffmpeg is null) return;

        Encode(Master(1), 3);
        var file = new FileInfo(Master(1));
        DateTime written = file.LastWriteTimeUtc;

        Assert.Equal(3, MediaLengths.Picture(file)!.Value.TotalSeconds, 1);

        // The same length and write time but nothing readable inside: only a remembered answer
        // can still say three seconds.
        File.WriteAllBytes(file.FullName, new byte[file.Length]);
        File.SetLastWriteTimeUtc(file.FullName, written);

        Assert.Equal(3, MediaLengths.Picture(new FileInfo(file.FullName))!.Value.TotalSeconds, 1);
    }

    [Fact]
    public void A_waveform_once_read_comes_back_from_the_disk_and_not_the_media()
    {
        if (Ffmpeg is null) return;

        string? before = Waveform.CacheFolder;
        Waveform.CacheFolder = Path.Combine(_temp, "peaks");

        try
        {
            Encode(Proxy(1), 2);
            var file = new FileInfo(Proxy(1));
            DateTime written = file.LastWriteTimeUtc;

            WavePeaks first = Waveform.Peaks(Ffmpeg, file.FullName)!;
            Assert.NotEmpty(Directory.GetFiles(Waveform.CacheFolder, "*.peaks"));

            // Forget it in memory, and leave nothing in the file to read it from again.
            Waveform.ForgetRemembered();
            File.WriteAllBytes(file.FullName, new byte[file.Length]);
            File.SetLastWriteTimeUtc(file.FullName, written);

            WavePeaks? again = Waveform.Peaks(Ffmpeg, file.FullName);

            Assert.NotNull(again);
            Assert.Equal(first.Fine.Length, again!.Fine.Length);
            Assert.InRange(again.Max(0, 2), first.Max(0, 2) - 0.01f, first.Max(0, 2) + 0.01f);
        }
        finally
        {
            Waveform.CacheFolder = before;
        }
    }

    [Fact]
    public void A_waveform_of_a_changed_file_is_read_again()
    {
        if (Ffmpeg is null) return;

        string? before = Waveform.CacheFolder;
        Waveform.CacheFolder = Path.Combine(_temp, "peaks");

        try
        {
            Encode(Proxy(1), 2);
            Assert.NotNull(Waveform.Peaks(Ffmpeg, Proxy(1)));

            // A new write time is a new file: the cached waveform is not its.
            Encode(Proxy(1), 1);
            File.SetLastWriteTimeUtc(Proxy(1), DateTime.UtcNow.AddMinutes(5));

            WavePeaks second = Waveform.Peaks(Ffmpeg, Proxy(1))!;
            Assert.InRange(second.Fine.Length, 40, 60);   // one second at fifty peaks a second
        }
        finally
        {
            Waveform.CacheFolder = before;
        }
    }
}
