using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Emerald.Setup;

/// <summary>
/// The published Emerald folder, zipped and appended to this executable by pack-setup.ps1.
///
/// The file is: [this program][zip][8-byte offset of the zip][8 bytes "EMSETUP1"]. The zip's own
/// offsets count from its first byte, so it is read through a window that starts there. One file
/// to copy onto a USB stick, and nothing for a setup tool to install on the build machine.
/// </summary>
internal sealed class Payload : IDisposable
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("EMSETUP1");

    private readonly FileStream _file;
    private readonly ZipArchive _zip;

    /// <summary>name=value lines from setup.ini, which publish.bat writes into the payload.</summary>
    public IReadOnlyDictionary<string, string> Info { get; }

    public long Bytes { get; }

    private Payload(FileStream file, ZipArchive zip, Dictionary<string, string> info, long bytes)
    {
        _file = file;
        _zip = zip;
        Info = info;
        Bytes = bytes;
    }

    public string this[string key] => Info.TryGetValue(key, out string value) ? value : "";

    /// <summary>The payload appended to <paramref name="exe"/>, or null when there is none.</summary>
    public static Payload Open(string exe)
    {
        var file = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);

        try
        {
            if (file.Length < 16) { file.Dispose(); return null; }

            var trailer = new byte[16];
            file.Seek(-16, SeekOrigin.End);
            ReadFully(file, trailer);

            for (int i = 0; i < 8; i++)
                if (trailer[8 + i] != Magic[i]) { file.Dispose(); return null; }

            long offset = BitConverter.ToInt64(trailer, 0);
            long length = file.Length - 16 - offset;
            if (offset <= 0 || length <= 0) { file.Dispose(); return null; }

            var zip = new ZipArchive(new Window(file, offset, length), ZipArchiveMode.Read, leaveOpen: true);

            var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ZipArchiveEntry ini = zip.GetEntry("setup.ini");

            if (ini is not null)
            {
                using var reader = new StreamReader(ini.Open(), Encoding.UTF8);
                string line;
                while ((line = reader.ReadLine()) is not null)
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) info[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }

            long bytes = 0;
            foreach (ZipArchiveEntry entry in zip.Entries) bytes += entry.Length;

            return new Payload(file, zip, info, bytes);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Unpacks everything into <paramref name="folder"/>, reporting bytes done.</summary>
    public void ExtractTo(string folder, Action<long> progress)
    {
        string root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        long done = 0;
        var buffer = new byte[1 << 20];

        foreach (ZipArchiveEntry entry in _zip.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', '\\')));

            // Never outside the folder, whatever a name in the zip says.
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

            if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target));

            using Stream from = entry.Open();
            using var to = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);

            int n;
            while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
            {
                to.Write(buffer, 0, n);
                done += n;
                progress(done);
            }
        }
    }

    public void Dispose()
    {
        _zip.Dispose();
        _file.Dispose();
    }

    private static void ReadFully(Stream stream, byte[] into)
    {
        int at = 0;
        while (at < into.Length)
        {
            int n = stream.Read(into, at, into.Length - at);
            if (n <= 0) throw new EndOfStreamException();
            at += n;
        }
    }

    /// <summary>A read-only, seekable view of part of a stream.</summary>
    private sealed class Window : Stream
    {
        private readonly Stream _inner;
        private readonly long _start;
        private readonly long _length;
        private long _position;

        public Window(Stream inner, long start, long length)
        {
            _inner = inner;
            _start = start;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Max(0, Math.Min(_length, value));
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long left = _length - _position;
            if (left <= 0) return 0;
            if (count > left) count = (int)left;

            _inner.Position = _start + _position;
            int n = _inner.Read(buffer, offset, count);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => _length + offset,
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
