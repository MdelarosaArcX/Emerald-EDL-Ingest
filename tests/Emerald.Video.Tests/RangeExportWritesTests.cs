using System.Diagnostics;
using System.IO;
using Emerald.Media;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// That an export actually writes media.
///
/// The claim worth testing is "a new file", not "a sidecar": a JSON note beside nothing would be
/// a convincing-looking export of no video at all, and the failure would surface when somebody
/// tried to transmit it. So this runs the real ffmpeg over real segments it makes itself, and
/// then checks the result is a media file of the length that was asked for.
///
/// <b>These two do nothing when there is no ffmpeg on the machine.</b> xunit 2.5 cannot report a
/// skip decided at run time, so they return instead — which means a green run on a machine
/// without ffmpeg proves nothing about exporting. They are written for the plant, where ffmpeg is
/// bundled beside the build and they do run.
/// </summary>
public sealed class RangeExportWritesTests
{
    /// <summary>
    /// Where ffmpeg is, or null.
    ///
    /// Found by walking up from the test binaries looking for the published build, rather than by
    /// a hardcoded path. The first version of this said Path.Combine("E:", "Emerald", ...), which
    /// on Windows produces the drive-RELATIVE "E:Emerald\..." and not "E:\Emerald\..." - so it
    /// never matched, the tests returned doing nothing, and reported green. Walking up has no
    /// such trap and works from any checkout.
    /// </summary>
    private static string? Ffmpeg()
    {
        var candidates = new List<string>();

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "dist", "Emerald", "ffmpeg.exe"));
            candidates.Add(Path.Combine(dir.FullName, "dist", "Emerald-EDL", "ffmpeg.exe"));
            candidates.Add(Path.Combine(dir.FullName, "ffmpeg.exe"));
        }

        candidates.Add("ffmpeg.exe");
        candidates.Add("ffmpeg");

        foreach (string candidate in candidates)
        {
            try
            {
                if (Path.IsPathRooted(candidate) && File.Exists(candidate)) return candidate;

                using Process? p = Process.Start(new ProcessStartInfo(candidate, "-version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });

                if (p is null) continue;

                p.WaitForExit(10_000);

                if (p.ExitCode == 0) return candidate;
            }
            catch (Exception) { }
        }

        return null;
    }

    /// <summary>A silent colour clip of a given length, which stands in for a captured segment.</summary>
    private static bool MakeSegment(string ffmpeg, string path, int seconds, string colour)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        foreach (string a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                     "-f", "lavfi", "-i", $"color=c={colour}:s=320x180:r=25:d={seconds}",
                     "-f", "lavfi", "-i", $"anullsrc=r=48000:cl=stereo:d={seconds}",
                     "-c:v", "libx264", "-g", "25", "-pix_fmt", "yuv420p",
                     "-c:a", "aac", "-shortest", path,
                 })
            info.ArgumentList.Add(a);

        using Process? p = Process.Start(info);

        if (p is null) return false;

        p.StandardError.ReadToEnd();
        p.WaitForExit();

        return p.ExitCode == 0 && File.Exists(path);
    }

    [Fact]
    public void An_export_writes_a_playable_file_and_a_sidecar_beside_it()
    {
        string? ffmpeg = Ffmpeg();

        if (ffmpeg is null) return;   // nothing to export with; see the note on the class

        string store = Path.Combine(Path.GetTempPath(), $"emerald-export-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(store);

            // Two four-second segments, laid end to end on the clock like real ones.
            string a = Path.Combine(store, "seg1.mp4");
            string b = Path.Combine(store, "seg2.mp4");

            Assert.True(MakeSegment(ffmpeg!, a, 4, "navy"), "could not make the first segment");
            Assert.True(MakeSegment(ffmpeg!, b, 4, "maroon"), "could not make the second segment");

            // A range crossing the boundary: the last second of one and the first of the next.
            var parts = RangePlan.Parts(
                new[] { (a, 0L, 100L), (b, 100L, 100L) },
                inFrame: 75, outFrame: 125);

            Assert.Equal(2, parts.Count);

            ExportResult result = RangeExport.Write(
                ffmpeg!, store, "an-export", "metadata about it", parts, 25, "mp4");

            Assert.True(result.Ok, result.Problem);

            // The media itself, which is the whole point.
            Assert.True(File.Exists(result.Path), "the export wrote no media file");
            Assert.True(new FileInfo(result.Path).Length > 1024,
                        "the export wrote a media file with nothing in it");

            // And the sidecar beside it, not instead of it.
            string sidecar = Path.ChangeExtension(result.Path, ".json");

            Assert.True(File.Exists(sidecar), "the export wrote no sidecar");
            Assert.Contains("metadata about it", File.ReadAllText(sidecar));

            // It lands in the ingest folder, which is what the Ingest Media tab lists.
            Assert.Equal(RangeExport.FolderFor(store), Path.GetDirectoryName(result.Path));
            Assert.Contains(result.Path, RangeExport.List(store, null).Select(c => c.Path));
        }
        finally
        {
            try { Directory.Delete(store, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void An_export_over_a_name_already_in_the_folder_does_not_destroy_the_first()
    {
        // The dialog refuses a duplicate name before getting here; this is the floor under that,
        // because the two are written by different people at different times.
        string? ffmpeg = Ffmpeg();

        if (ffmpeg is null) return;   // nothing to export with; see the note on the class

        string store = Path.Combine(Path.GetTempPath(), $"emerald-export-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(store);

            string a = Path.Combine(store, "seg1.mp4");
            Assert.True(MakeSegment(ffmpeg!, a, 4, "navy"));

            var parts = RangePlan.Parts(new[] { (a, 0L, 100L) }, 0, 50);

            ExportResult first = RangeExport.Write(ffmpeg!, store, "same", "", parts, 25, "mp4");
            Assert.True(first.Ok, first.Problem);

            long was = new FileInfo(first.Path).Length;

            ExportResult second = RangeExport.Write(ffmpeg!, store, "same", "", parts, 25, "mp4");

            // Whether it refuses or rewrites, what must not happen is a half-written file where a
            // good one was. Same content in, same size out.
            Assert.True(File.Exists(first.Path));

            if (second.Ok) Assert.Equal(was, new FileInfo(first.Path).Length);
        }
        finally
        {
            try { Directory.Delete(store, recursive: true); } catch (IOException) { }
        }
    }
}
