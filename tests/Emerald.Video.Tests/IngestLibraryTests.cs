using System.IO;
using Emerald.Media;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>The ingest media path: one folder per recording, high\ and low\, only low-res listed.</summary>
public sealed class IngestLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"emerald-ingest-{Guid.NewGuid():N}");

    public IngestLibraryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Touch(params string[] parts)
    {
        string path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "media");
        return path;
    }

    [Fact]
    public void Ensure_makes_the_recording_folder_with_high_and_low()
    {
        string? folder = IngestLibrary.Ensure(_root, "2026-11-21_14-05-00");

        Assert.Equal(Path.Combine(_root, "2026-11-21_14-05-00"), folder);
        Assert.True(Directory.Exists(Path.Combine(folder!, "high")));
        Assert.True(Directory.Exists(Path.Combine(folder!, "low")));
    }

    [Fact]
    public void Folders_are_newest_first_with_the_one_on_air_at_the_top_and_loose_files_last()
    {
        IngestLibrary.Ensure(_root, "2026-11-19_09-00-00");
        IngestLibrary.Ensure(_root, "2026-11-21_14-05-00");
        IngestLibrary.Ensure(_root, "2026-11-20_09-00-00");
        Touch("2026-11-20_09-00-00", "low", "a.mp4");
        Touch("old-export.mov");

        IReadOnlyList<IngestFolder> folders = IngestLibrary.Folders(_root, liveFolder: "2026-11-19_09-00-00");

        Assert.Equal(new[] { "2026-11-19_09-00-00", "2026-11-21_14-05-00", "2026-11-20_09-00-00", "" },
                     folders.Select(f => f.Name));
        Assert.True(folders[0].IsLive);
        Assert.Equal(1, folders[2].Clips);
        Assert.True(folders[3].IsLoose);
        Assert.Equal("Earlier exports", folders[3].DisplayName);
    }

    [Fact]
    public void Only_the_low_res_is_listed_and_each_knows_its_high_res()
    {
        string high = Touch("2026-11-21_14-05-00", "high", "CLIP_1.mov");
        string low = Touch("2026-11-21_14-05-00", "low", "CLIP_1.mp4");
        Touch("2026-11-21_14-05-00", "low", ".CLIP_2.part.mp4");     // being written
        Touch("2026-11-21_14-05-00", "high", "CLIP_1.json");

        IngestFolder folder = IngestLibrary.Folders(_root, null).Single();
        CapturedClip clip = Assert.Single(IngestLibrary.Clips(folder, null, 25));

        Assert.Equal(low, clip.Path);
        Assert.Equal(high, clip.MasterPath);
        Assert.Equal(high, IngestLibrary.MasterFor(low));
        Assert.Equal(low, IngestLibrary.LowFor(high));
        Assert.Equal(low, RangeExport.PlayablePathFor(high));
    }

    [Fact]
    public void A_low_res_with_no_high_res_has_no_master_and_is_placed_as_itself()
    {
        string low = Touch("2026-11-21_14-05-00", "low", "only-low.mp4");

        Assert.Null(IngestLibrary.MasterFor(low));
        Assert.Null(IngestLibrary.MasterFor(Touch("elsewhere", "x.mp4")));
    }

    [Fact]
    public void Pending_finds_dropped_files_and_high_res_without_a_low_res_but_not_files_being_written()
    {
        string dropped = Touch("2026-11-21_14-05-00", "dropped.mov");
        string bare = Touch("2026-11-21_14-05-00", "high", "bare.mxf");
        Touch("2026-11-21_14-05-00", "high", "done.mov");
        Touch("2026-11-21_14-05-00", "low", "done.mp4");
        string writing = Touch("2026-11-21_14-05-00", "high", "recording.mov");

        using var held = new FileStream(writing, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        IReadOnlyList<string> pending = IngestLibrary.Pending(_root);

        Assert.Contains(dropped, pending);
        Assert.Contains(bare, pending);
        Assert.Equal(2, pending.Count);
    }

    [Fact]
    public void A_dropped_file_moves_into_high_without_overwriting_anything()
    {
        Touch("2026-11-21_14-05-00", "high", "clip.mov");
        string dropped = Touch("2026-11-21_14-05-00", "clip.mov");

        string moved = IngestLibrary.MoveIntoHigh(dropped);

        Assert.Equal(Path.Combine(_root, "2026-11-21_14-05-00", "high", "clip (2).mov"), moved);
        Assert.False(File.Exists(dropped));
        Assert.Equal(Path.Combine(_root, "2026-11-21_14-05-00", "low", "clip (2).mp4"), IngestLibrary.LowPathFor(moved));
    }

    [Fact]
    public void An_ingest_job_pointed_at_the_ingest_path_gets_a_recording_folder_and_one_pointed_elsewhere_does_not()
    {
        string other = Path.Combine(_root, "somewhere-else");

        Assert.Equal(other, IngestLibrary.JobFolder(other, _root));

        string resolved = IngestLibrary.JobFolder(_root + "\\", _root);

        Assert.Equal(_root, Path.GetDirectoryName(resolved));
        Assert.NotNull(Core.RecordingSequence.FolderStartedAt(Path.GetFileName(resolved)));
        Assert.True(Directory.Exists(Path.Combine(resolved, "high")));
    }
}
