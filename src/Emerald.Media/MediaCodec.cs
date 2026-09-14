using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Emerald.Media;

/// <summary>
/// What a QuickTime or MP4 file was encoded with, read out of the container itself.
///
/// <b>Why not just ask ffprobe.</b> Because the store has a thousand files in it and a probe
/// costs about two hundred milliseconds — the capture deck would spend three minutes listing
/// itself. This opens the file, walks the atom tree to the video sample description, reads a
/// four-character code and closes it again: a handful of seeks and a few kilobytes, with no
/// process to start.
///
/// It answers one question only. Anything that needs duration, raster or stream layout still
/// goes through <see cref="MediaProbe"/>, which is worth its cost for one file and not for a
/// thousand.
/// </summary>
public static class MediaCodec
{
    /// <summary>Far enough into the tree to be lost. Guards against a malformed file looping.</summary>
    private const int MaxDepth = 8;

    /// <summary>
    /// The codec of the first video track, as a friendly name — "ProRes 422", "DNxHR" — or
    /// null when the file is not a QuickTime-family container, has no video, or has not
    /// finished being written.
    ///
    /// That last case is normal rather than exceptional: ffmpeg writes the index at the end,
    /// so the segment currently being recorded genuinely has nothing to read yet.
    /// </summary>
    public static string? Read(string path) =>
        ReadFourCc(path) is { } fourCc ? Describe(fourCc) : null;

    /// <summary>
    /// Remembers what each file turned out to be.
    ///
    /// A finished recording never changes codec, and the store is listed again every time it
    /// changes — so without this the same thousand files are re-opened on every refresh. Keyed
    /// on length and write time as well as path, so a file replaced under the same name is
    /// read again rather than believed.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> Cache = new();

    /// <summary>The raw four-character code, for anything that wants to compare rather than show.</summary>
    public static string? ReadFourCc(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;

            string key = $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";

            return Cache.GetOrAdd(key, _ => ReadUncached(path));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? ReadUncached(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return FourCc(file);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>
    /// Turns a four-character code into something to put in a column.
    ///
    /// Unknown codes come back as themselves rather than as "unknown": a file Emerald did not
    /// write is still better described by its own fourcc than by a shrug.
    /// </summary>
    public static string Describe(string fourCc) => fourCc switch
    {
        "apco" => "ProRes 422 Proxy",
        "apcs" => "ProRes 422 LT",
        "apcn" => "ProRes 422",
        "apch" => "ProRes 422 HQ",
        "ap4h" => "ProRes 4444",
        "ap4x" => "ProRes 4444 XQ",

        "AVdh" => "DNxHR",
        "AVdn" => "DNxHD",

        "avc1" or "h264" => "H.264",
        "hvc1" or "hev1" => "HEVC",
        "mp4v" => "MPEG-4",
        "dvh1" or "dvhp" => "DVCPRO HD",

        _ => fourCc,
    };

    /// <summary>True for anything Emerald would call a mastering codec rather than a proxy.</summary>
    public static bool IsMaster(string? fourCc) =>
        fourCc is "apcn" or "apch" or "apcs" or "ap4h" or "ap4x" or "AVdh" or "AVdn";

    // ------------------------------------------------------------------ the atom tree

    /// <summary>
    /// Walks moov → trak → mdia → minf → stbl → stsd and reads the first entry's code.
    ///
    /// Structural rather than a scan for known byte sequences: an index table full of offsets
    /// can very easily contain the four bytes "AVdh" by coincidence, and a file that reported
    /// the wrong codec would be worse than one that reported none.
    /// </summary>
    private static string? FourCc(Stream file) => FindStsd(file, 0, file.Length, 0);

    private static string? FindStsd(Stream file, long start, long end, int depth)
    {
        if (depth > MaxDepth) return null;

        long at = start;

        // Allocated once for the whole walk rather than once per atom: a stackalloc inside a
        // loop grows the frame every pass and a deeply nested file would exhaust it.
        Span<byte> header = stackalloc byte[16];

        while (at + 8 <= end)
        {
            file.Position = at;
            if (!Fill(file, header[..8])) return null;

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Encoding.ASCII.GetString(header[4..8]);
            long body = at + 8;

            if (size == 1)
            {
                // A 64-bit size follows the type. Rare, but a two-minute ProRes segment is
                // already over a gigabyte and a longer one would reach it.
                if (!Fill(file, header[8..16])) return null;

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
                body += 8;
            }
            else if (size == 0)
            {
                size = end - at;   // "to the end of the file"
            }

            if (size < 8 || at + size > end) return null;

            if (type == "stsd") return FirstEntry(file, body);

            // Only the containers on the way down are opened; mdat is simply stepped over,
            // which is what keeps this cheap on a file of this size.
            if (type is "moov" or "trak" or "mdia" or "minf" or "stbl")
            {
                if (FindStsd(file, body, at + size, depth + 1) is { } found) return found;
            }

            at += size;
        }

        return null;
    }

    /// <summary>
    /// The first sample description: a version/flags word, an entry count, then entries that
    /// each begin with their own size and the four-character code.
    /// </summary>
    private static string? FirstEntry(Stream file, long body)
    {
        file.Position = body;

        Span<byte> head = stackalloc byte[16];
        if (!Fill(file, head)) return null;

        uint entries = BinaryPrimitives.ReadUInt32BigEndian(head[4..8]);
        if (entries == 0) return null;

        return Encoding.ASCII.GetString(head[12..16]);
    }

    private static bool Fill(Stream file, Span<byte> into)
    {
        int read = 0;

        while (read < into.Length)
        {
            int got = file.Read(into[read..]);
            if (got <= 0) return false;

            read += got;
        }

        return true;
    }
}
