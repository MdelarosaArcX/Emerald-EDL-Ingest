using System.Diagnostics;
using Emerald.Core;

namespace Emerald.Video;

/// <summary>
/// The sound of recorded files, every track of it, as samples in memory: what Live Edit's preview
/// plays from, so changing from one track to another is reading a different pair of channels at the
/// next sample rather than opening another file.
///
/// Read ten seconds at a time, a little ahead of where it is asked for, and the most recent few kept:
/// four stereo tracks are under 8 MB for ten seconds. Each piece is one ffmpeg pass over the file for
/// all the tracks together, one at a time, behind the recorder for the disk.
/// </summary>
public sealed class SegmentSound : ISoundSource, IDisposable
{
    public const int SampleRate = 48000;

    /// <summary>How much is read at a time.</summary>
    public const int PieceSeconds = 10;

    private const long PieceSamples = (long)PieceSeconds * SampleRate;

    /// <summary>Pieces kept: the one playing, the next, and a little either side for clicking about.</summary>
    private const int MostKept = 6;

    /// <summary>
    /// How much before a piece is decoded and thrown away. A sound decoder needs the packet before to
    /// give the first samples right, and a piece starting cold would begin with a tick.
    /// </summary>
    private const double LeadSeconds = 0.25;

    /// <summary>
    /// At its own time: a recording's sound can start some milliseconds after its picture, and written
    /// out as it comes the first piece would be that much early and every other one not.
    /// </summary>
    private static readonly string Placed = $"aresample={SampleRate}:async=1:first_pts=0";

    private sealed class Piece
    {
        public byte[]? Samples;
        public bool Failed;
        public long Used;
    }

    private readonly string _ffmpeg;
    private readonly object _gate = new();
    private readonly Dictionary<(string Path, int Tracks, long Index), Piece> _pieces = new();
    private readonly SemaphoreSlim _one = new(1);
    private long _clock;
    private bool _disposed;

    public SegmentSound(string ffmpeg) => _ffmpeg = ffmpeg;

    /// <summary>Bytes held, for the memory monitor.</summary>
    public long Bytes
    {
        get { lock (_gate) return _pieces.Values.Sum(p => (long)(p.Samples?.Length ?? 0)); }
    }

    /// <summary>Has the sound at a sample ready, reading it if it is not. Returns at once.</summary>
    public void Want(string path, int tracks, long sample) => Find(path, tracks, Math.Max(0, sample) / PieceSamples);

    /// <summary>
    /// Copies <paramref name="count"/> samples of every channel from <paramref name="sample"/> on, as
    /// 16-bit interleaved. False when they are not in memory yet - and then they are being read. Past
    /// the end of the file is silence.
    /// </summary>
    public bool Read(string path, int tracks, long sample, int count, byte[] into)
    {
        int width = tracks * 4;
        Array.Clear(into, 0, count * width);

        int done = 0;

        // Before the file starts is silence too: a player is heard a moment ahead of what it shows.
        if (sample < 0)
        {
            done = (int)Math.Min(count, -sample);
            sample += done;
        }

        while (done < count)
        {
            long index = sample / PieceSamples, into0 = sample % PieceSamples;
            int take = (int)Math.Min(count - done, PieceSamples - into0);

            Piece piece = Find(path, tracks, index);
            byte[]? samples;

            lock (_gate)
            {
                if (piece.Samples is null && !piece.Failed) return false;
                samples = piece.Samples;
            }

            if (samples is not null)
            {
                long from = into0 * width;
                int bytes = (int)Math.Clamp(samples.Length - from, 0, take * (long)width);
                if (bytes > 0) Buffer.BlockCopy(samples, (int)from, into, done * width, bytes);
            }

            done += take;
            sample += take;
        }

        // The next piece, in good time.
        if (sample % PieceSamples > PieceSamples / 3) Find(path, tracks, sample / PieceSamples + 1);

        return true;
    }

    /// <summary>How many samples a file's sound has, once the piece it ends in has been read.</summary>
    public long? Length(string path, int tracks)
    {
        long full = PieceSamples * tracks * 4;

        lock (_gate)
        {
            (long Index, long Bytes)? last = null;

            foreach (var (key, piece) in _pieces)
                if (key.Path == path && key.Tracks == tracks && piece.Samples is { } s && s.Length < full &&
                    (last is null || key.Index < last.Value.Index))
                    last = (key.Index, s.Length);

            return last is { } end ? end.Index * PieceSamples + end.Bytes / (tracks * 4) : null;
        }
    }

    /// <summary>
    /// How far into a file its sound starts, in samples: a segment begins on a frame of picture, and
    /// the sound packets do not, so it can be some milliseconds in. Null until its start has been read.
    /// </summary>
    public long? Lead(string path, int tracks)
    {
        lock (_gate)
        {
            if (!_pieces.TryGetValue((path, tracks, 0), out Piece? first)) return null;
            if (first.Samples is not { } samples) return first.Failed ? 0 : null;

            const int most = 2048;
            int width = tracks * 4, zero = 0;

            while (zero < most && (zero + 1) * width <= samples.Length &&
                   samples.AsSpan(zero * width, width).IndexOfAnyExcept((byte)0) < 0)
                zero++;

            // Silence rather than a late start.
            return zero < most ? zero : 0;
        }
    }

    private Piece Find(string path, int tracks, long index)
    {
        var key = (path, tracks, index);
        Piece? piece;

        lock (_gate)
        {
            if (_pieces.TryGetValue(key, out piece))
            {
                piece.Used = ++_clock;
                return piece;
            }

            piece = new Piece { Used = ++_clock };
            if (_disposed) { piece.Failed = true; return piece; }

            _pieces[key] = piece;

            foreach (var old in _pieces.Where(p => p.Value.Samples is not null || p.Value.Failed)
                                       .OrderByDescending(p => p.Value.Used).Skip(MostKept).Select(p => p.Key).ToList())
                _pieces.Remove(old);
        }

        _ = Task.Run(async () =>
        {
            await _one.WaitAsync();

            byte[]? samples = null;

            try
            {
                if (!_disposed) samples = Decode(_ffmpeg, path, tracks, index);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                ActivityLog.Shared.Warn(LogSource.LiveEdit,
                    $"The sound of {Path.GetFileName(path)} could not be read for the preview: {ex.Message}",
                    @event: "liveedit.soundfailed", file: path);
            }
            finally
            {
                _one.Release();
            }

            lock (_gate)
            {
                piece.Samples = samples;
                piece.Failed = samples is null;
            }
        });

        return piece;
    }

    /// <summary>One piece of every track, interleaved two channels a track. Empty past the end of the file.</summary>
    private static byte[]? Decode(string ffmpeg, string path, int tracks, long index)
    {
        double start = index * PieceSeconds;
        double lead = Math.Min(start, LeadSeconds);
        int width = tracks * 4;

        var info = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        string Number(double value) => value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

        var args = new List<string> { "-v", "error", "-nostdin" };

        // Not at the start: a seek to nought skips the packet a sound track's first samples need.
        if (start > 0) args.AddRange(new[] { "-ss", Number(start - lead) });

        args.AddRange(new[] { "-t", Number(PieceSeconds + lead), "-i", path, "-vn" });

        if (tracks == 1)
        {
            args.AddRange(new[] { "-map", "0:a:0", "-af", $"{Placed},aformat=channel_layouts=stereo" });
        }
        else
        {
            var graph = new System.Text.StringBuilder();
            for (int i = 0; i < tracks; i++)
                graph.Append($"[0:a:{i}]{Placed},aformat=channel_layouts=stereo[s{i}];");
            for (int i = 0; i < tracks; i++) graph.Append($"[s{i}]");

            args.AddRange(new[] { "-filter_complex", $"{graph}amerge=inputs={tracks}[all]", "-map", "[all]" });
        }

        args.AddRange(new[] { "-f", "s16le", "-acodec", "pcm_s16le", "-" });
        foreach (string arg in args) info.ArgumentList.Add(arg);

        using Process? process = Process.Start(info);
        if (process is null) return null;

        BackgroundPriority.Apply(process);

        Task<string> problem = process.StandardError.ReadToEndAsync();

        using var all = new MemoryStream((int)((PieceSamples + SampleRate) * width));
        process.StandardOutput.BaseStream.CopyTo(all);
        process.WaitForExit(15000);

        if (all.Length == 0 && problem.Wait(1000) && problem.Result.Trim() is { Length: > 0 } said)
            throw new InvalidOperationException(said);

        long skip = (long)Math.Round(lead * SampleRate) * width;
        long keep = Math.Clamp(all.Length - skip, 0, PieceSamples * width);

        var samples = new byte[keep];
        Buffer.BlockCopy(all.GetBuffer(), (int)skip, samples, 0, (int)keep);
        return samples;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pieces.Clear();
        }
    }
}
