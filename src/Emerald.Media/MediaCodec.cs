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


    // ------------------------------------------------------------------ duration

    /// <summary>
    /// How long the file plays for, from the movie header — or null when the file is not a
    /// QuickTime-family container or has not finished being written.
    ///
    /// The same walk as the codec, for the same reason: ffprobe is two hundred milliseconds
    /// a file and this is eight, and it is asked on every tick of the deck while recording.
    /// Null for an unfinished file is the useful answer rather than a failure — the movie
    /// header is written last, so its absence is exactly what "still being recorded" looks
    /// like from the outside.
    /// </summary>
    public static TimeSpan? ReadDuration(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return FindMvhd(file, 0, file.Length, 0);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private static TimeSpan? FindMvhd(Stream file, long start, long end, int depth)
    {
        if (depth > MaxDepth) return null;

        long at = start;
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
                if (!Fill(file, header[8..16])) return null;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
                body += 8;
            }
            else if (size == 0)
            {
                size = end - at;
            }

            if (size < 8 || at + size > end) return null;

            if (type == "mvhd") return MovieDuration(file, body);

            if (type == "moov")
            {
                if (FindMvhd(file, body, at + size, depth + 1) is { } found) return found;
            }

            at += size;
        }

        return null;
    }

    /// <summary>
    /// Version 0 lays out creation, modification, timescale and duration as four 32-bit
    /// words; version 1 widens the two times and the duration to 64 bits. The timescale is
    /// 32 bits in both.
    /// </summary>
    private static TimeSpan? MovieDuration(Stream file, long body)
    {
        file.Position = body;

        Span<byte> head = stackalloc byte[32];
        if (!Fill(file, head)) return null;

        byte version = head[0];

        uint timescale;
        ulong duration;

        if (version == 1)
        {
            timescale = BinaryPrimitives.ReadUInt32BigEndian(head[20..24]);
            duration = BinaryPrimitives.ReadUInt64BigEndian(head[24..32]);
        }
        else
        {
            timescale = BinaryPrimitives.ReadUInt32BigEndian(head[12..16]);
            duration = BinaryPrimitives.ReadUInt32BigEndian(head[16..20]);
        }

        if (timescale == 0) return null;

        return TimeSpan.FromSeconds(duration / (double)timescale);
    }

    /// <summary>
    /// How long the file's picture runs, from the video track's media header - or null when the
    /// file has no video track or has not finished being written.
    ///
    /// <b>Why not the movie header.</b> That is the longest track, and in a recorder's segment
    /// that is the sound: the encoder's audio frames do not end on a picture frame, so each file's
    /// audio runs a few milliseconds past its last frame. Measured on this plant, ten-second
    /// segments came out 0 to 16 ms long, 5.45 s over 685 of them. Laid end to end to place
    /// segments on the clock, that was the timeline sliding behind air a little more with every
    /// file. The picture is exactly the frames recorded, so it is what the next segment follows on from.
    /// </summary>
    public static TimeSpan? ReadPictureDuration(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return FindPicture(file, 0, file.Length, 0);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>Down through moov to each trak, and the first whose media is video.</summary>
    private static TimeSpan? FindPicture(Stream file, long start, long end, int depth)
    {
        if (depth > MaxDepth) return null;

        foreach ((string type, long body, long next) in Atoms(file, start, end))
        {
            if (type == "moov" && FindPicture(file, body, next, depth + 1) is { } found) return found;

            if (type == "trak" && TrackMedia(file, body, next) is { Handler: "vide" } track) return track.Duration;
        }

        return null;
    }

    /// <summary>A track's handler type and its media header's duration, from its mdia.</summary>
    private static (string Handler, TimeSpan? Duration)? TrackMedia(Stream file, long start, long end)
    {
        foreach ((string type, long body, long next) in Atoms(file, start, end))
        {
            if (type != "mdia") continue;

            string? handler = null;
            TimeSpan? duration = null;

            foreach ((string inner, long innerBody, _) in Atoms(file, body, next))
            {
                // Media header: laid out as the movie header is, timescale and duration in the
                // same places for both versions.
                if (inner == "mdhd") duration = MovieDuration(file, innerBody);

                // Handler: version/flags, pre-defined, then the four-character type.
                if (inner == "hdlr")
                {
                    file.Position = innerBody + 8;

                    Span<byte> kind = stackalloc byte[4];
                    if (Fill(file, kind)) handler = Encoding.ASCII.GetString(kind);
                }
            }

            return handler is null ? null : (handler, duration);
        }

        return null;
    }

    /// <summary>The atoms laid side by side between two offsets: type, where its body starts, where the next begins.</summary>
    private static List<(string Type, long Body, long Next)> Atoms(Stream file, long start, long end)
    {
        var found = new List<(string, long, long)>();
        long at = start;
        var header = new byte[16];

        while (at + 8 <= end)
        {
            file.Position = at;
            if (!Fill(file, header.AsSpan(0, 8))) break;

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Encoding.ASCII.GetString(header, 4, 4);
            long body = at + 8;

            if (size == 1)
            {
                if (!Fill(file, header.AsSpan(8, 8))) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                body += 8;
            }
            else if (size == 0)
            {
                size = end - at;
            }

            if (size < 8 || at + size > end) break;

            found.Add((type, body, at + size));
            at += size;
        }

        return found;
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
