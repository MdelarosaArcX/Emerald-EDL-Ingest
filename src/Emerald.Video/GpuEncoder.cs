using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Emerald.Video;

/// <summary>The makers of video card the proxy can be encoded on.</summary>
public enum GpuVendor
{
    Nvidia,
    Intel,
}

/// <summary>
/// One way of encoding the proxy on a video card. Whichever it is, the file is a half-second-GOP,
/// no-B-frame MP4 in the same folder under the same name, so nothing after the encoder has to care.
/// </summary>
/// <param name="Quality">
/// The encoder's constant quality setting for when the bitrate is left on Auto detect. NVIDIA's were
/// measured on this plant's own master against x264 at its default: H.264 at 28 is the same size and
/// slightly better; HEVC at 30 is about a quarter smaller and within 0.002 SSIM. Intel's have not
/// been measured, because this plant has no Intel graphics: they are a starting point.
/// </param>
public sealed record ProxyEncoder(
    GpuVendor Vendor,
    string Codec,
    string Label,
    bool Hevc,
    string PixelFormat,
    int Quality)
{
    public static ProxyEncoder HevcNvenc { get; } = new(GpuVendor.Nvidia, "hevc_nvenc", "HEVC (NVIDIA)", true, "yuv420p", 30);
    public static ProxyEncoder H264Nvenc { get; } = new(GpuVendor.Nvidia, "h264_nvenc", "H.264 (NVIDIA)", false, "yuv420p", 28);

    // Quick Sync takes NV12 rather than planar 4:2:0. Same samples, same file.
    public static ProxyEncoder HevcQsv { get; } = new(GpuVendor.Intel, "hevc_qsv", "HEVC (Intel)", true, "nv12", 28);
    public static ProxyEncoder H264Qsv { get; } = new(GpuVendor.Intel, "h264_qsv", "H.264 (Intel)", false, "nv12", 25);

    /// <summary>Everything between <c>-c:v</c> and the pixel format, including the codec itself.</summary>
    public IReadOnlyList<string> Arguments(int frameRate, int bitrateKbps)
    {
        var args = new List<string> { "-c:v", Codec };

        args.AddRange(Vendor == GpuVendor.Nvidia
            ? new[] { "-preset", "p4", "-no-scenecut", "1", "-forced-idr", "1", "-rc", "vbr" }
            : new[] { "-preset", "medium" });

        args.AddRange(new[] { "-profile:v", Hevc ? "main" : "high", "-bf", "0" });
        args.AddRange(new[] { "-g", Math.Max(1, frameRate / 2).ToString() });

        // hvc1 rather than ffmpeg's hev1: it is the one Windows' own players will open.
        if (Hevc) args.AddRange(new[] { "-tag:v", "hvc1" });

        if (bitrateKbps > 0)
        {
            args.AddRange(new[]
            {
                "-b:v", $"{bitrateKbps}k", "-maxrate", $"{bitrateKbps}k", "-bufsize", $"{bitrateKbps * 2}k",
            });
        }
        else if (Vendor == GpuVendor.Nvidia)
        {
            args.AddRange(new[] { "-cq", Quality.ToString(), "-b:v", "0" });
        }
        else
        {
            args.AddRange(new[] { "-global_quality", Quality.ToString() });
        }

        return args;
    }
}

/// <summary>
/// Which video card this machine has, and the best thing it can really encode the proxy with.
///
/// <b>Three questions, because each has been wrong on its own.</b> What is installed says which
/// encoders are worth trying. Trying them says which work: this plant's ffmpeg lists h264_nvenc
/// whatever the driver, then refuses to open it. And whether Windows can <i>play</i> the result
/// decides whether HEVC is on the list at all: the proxy is the picture Live Edit and every monitor
/// show, through Media Foundation, which has no HEVC decoder here. An HEVC proxy on this machine
/// recorded perfectly and then would not open.
/// </summary>
public static class GpuEncoder
{
    /// <summary>Longer than it has ever been seen to take, and short of keeping an operator waiting.</summary>
    private const int ProbeMilliseconds = 5000;

    /// <summary>Past this no further encoder is tried. The receiver is waiting on the answer.</summary>
    private const int ChoosingMilliseconds = 8000;

    private const string DisplayAdapters = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>The maker a display adapter's name belongs to, or null for one nothing here can use.</summary>
    public static GpuVendor? VendorOf(string adapter) =>
        adapter.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Nvidia
        : adapter.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Intel
        : null;

    /// <summary>
    /// The encoders worth trying, best first. NVIDIA ahead of Intel, because on a machine with both
    /// the separate card has the encoder to itself. HEVC ahead of H.264 where it can be played.
    /// </summary>
    public static IReadOnlyList<ProxyEncoder> Candidates(IEnumerable<GpuVendor> installed, bool hevcPlays)
    {
        var list = new List<ProxyEncoder>();

        foreach (GpuVendor vendor in installed.Distinct().OrderBy(v => v))
        {
            (ProxyEncoder hevc, ProxyEncoder h264) = vendor == GpuVendor.Nvidia
                ? (ProxyEncoder.HevcNvenc, ProxyEncoder.H264Nvenc)
                : (ProxyEncoder.HevcQsv, ProxyEncoder.H264Qsv);

            if (hevcPlays) list.Add(hevc);
            list.Add(h264);
        }

        return list;
    }

    /// <summary>
    /// The display adapters Windows has drivers for. May include a card since removed or disabled,
    /// which is harmless: trying its encoder is what settles it.
    /// </summary>
    public static IReadOnlyList<string> InstalledAdapters()
    {
        var names = new List<string>();

        try
        {
            using RegistryKey? adapters = Registry.LocalMachine.OpenSubKey(DisplayAdapters);
            if (adapters is null) return names;

            foreach (string sub in adapters.GetSubKeyNames())
            {
                // Numbered 0000, 0001...; "Properties" and "Configuration" sit alongside them.
                if (!sub.All(char.IsAsciiDigit)) continue;

                using RegistryKey? adapter = adapters.OpenSubKey(sub);

                if (adapter?.GetValue("DriverDesc") is string name && name.Length > 0)
                    names.Add(name);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // Unreadable reads as nothing installed, which is the CPU: slower, and certain to work.
        }

        return names;
    }

    /// <summary>
    /// Whether a viewer in this application could open an HEVC proxy.
    ///
    /// Tested against WPF's MediaElement on this plant: no decoder here, and it fails to open the
    /// file. Store-packaged decoders are deliberately not asked about. If one is missed the proxy
    /// is H.264, which plays; if one were counted that the players cannot load, nothing would.
    /// </summary>
    public static bool HevcPlays()
    {
        try
        {
            if (MFStartup(MfVersion, 0) != 0) return false;

            try
            {
                var input = new MftRegisterTypeInfo { Major = MediaTypeVideo, Sub = VideoFormatHevc };

                int hr = MFTEnumEx(CategoryVideoDecoder, EnumAllDesktop, ref input, IntPtr.Zero,
                                   out IntPtr activates, out uint count);

                if (activates != IntPtr.Zero)
                {
                    for (int i = 0; i < count; i++)
                        Marshal.Release(Marshal.ReadIntPtr(activates, i * IntPtr.Size));

                    Marshal.FreeCoTaskMem(activates);
                }

                return hr == 0 && count > 0;
            }
            finally
            {
                MFShutdown();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows N, with no media features.
            return false;
        }
    }

    /// <summary>
    /// The best encoder that works on this machine for this format, or null for the CPU, with a
    /// sentence saying how that was arrived at.
    /// </summary>
    public static ProxyEncoder? Choose(string ffmpegPath, CaptureFormat format, int bitrateKbps, out string account)
    {
        IReadOnlyList<string> adapters = InstalledAdapters();
        List<GpuVendor> vendors = adapters.Select(VendorOf).OfType<GpuVendor>().ToList();

        if (vendors.Count == 0)
        {
            account = adapters.Count == 0
                ? "no video card was found"
                : $"no NVIDIA or Intel video card here ({string.Join(", ", adapters)})";
            return null;
        }

        bool hevcPlays = HevcPlays();
        string found = string.Join(", ", adapters.Where(a => VendorOf(a) is not null));
        var notes = new List<string>();
        var choosing = Stopwatch.StartNew();

        foreach (ProxyEncoder encoder in Candidates(vendors, hevcPlays))
        {
            if (choosing.ElapsedMilliseconds > ChoosingMilliseconds)
            {
                notes.Add("the card was too slow to answer, so nothing further was tried");
                break;
            }

            if (WhyNot(ffmpegPath, encoder, format, bitrateKbps) is not { } why)
            {
                account = $"{found}" +
                          (encoder.Hevc || hevcPlays ? "" : "; H.264, because Windows here has no HEVC decoder for the players") +
                          (notes.Count > 0 ? $"; {string.Join("; ", notes)}" : "");
                return encoder;
            }

            notes.Add($"{encoder.Label}: {why}");
        }

        account = $"{found}; {string.Join("; ", notes)}";
        return null;
    }

    /// <summary>Why this encoder cannot encode this format with these settings, or null when it just did.</summary>
    public static string? WhyNot(string ffmpegPath, ProxyEncoder encoder, CaptureFormat format, int bitrateKbps = 0)
    {
        var info = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // The recording's own options rather than a bare encoder name, so an option this card
        // refuses is found out here and not by the recording.
        foreach (string a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin",
                     "-f", "lavfi", "-i", $"color=c=black:s={format.Width}x{format.Height}:r={format.FrameRate}",
                     "-frames:v", "2",
                 }
                 .Concat(encoder.Arguments(format.FrameRate, bitrateKbps))
                 .Concat(new[] { "-pix_fmt", encoder.PixelFormat, "-f", "null", "-" }))
            info.ArgumentList.Add(a);

        try
        {
            using Process? ffmpeg = Process.Start(info);
            if (ffmpeg is null) return "ffmpeg did not start.";

            var said = new List<string>();

            ffmpeg.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) lock (said) said.Add(e.Data);
            };
            ffmpeg.BeginErrorReadLine();

            if (!ffmpeg.WaitForExit(ProbeMilliseconds))
            {
                try { ffmpeg.Kill(true); } catch (InvalidOperationException) { }
                return $"the card had not answered after {ProbeMilliseconds / 1000} s.";
            }

            // Without a timeout, which is what waits for the last of stderr to arrive.
            ffmpeg.WaitForExit();

            if (ffmpeg.ExitCode == 0) return null;

            lock (said) return Reason(said);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// The line of ffmpeg's complaint worth repeating. The driver's, when there is one, because
    /// it is the one that says what to do; ffmpeg's closing lines only say that it stopped.
    /// </summary>
    public static string Reason(IReadOnlyList<string> said)
    {
        string? line = said.FirstOrDefault(l => l.Contains("minimum required", StringComparison.OrdinalIgnoreCase))
                    ?? said.FirstOrDefault();

        if (line is null) return "ffmpeg gave no reason.";

        // "[h264_nvenc @ 000001661a960bc0] The minimum..." - the address means nothing to anybody.
        int prefix = line.StartsWith('[') ? line.IndexOf("] ", StringComparison.Ordinal) : -1;

        return prefix >= 0 ? line[(prefix + 2)..] : line;
    }

    // ------------------------------------------------------------------ Media Foundation

    private const uint MfVersion = 0x00020070;

    /// <summary>Synchronous, asynchronous, hardware, field of use, local and transcode-only. Not store.</summary>
    private const uint EnumAllDesktop = 0x3F;

    private static readonly Guid CategoryVideoDecoder = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid VideoFormatHevc = new("43564548-0000-0010-8000-00aa00389b71");

    [StructLayout(LayoutKind.Sequential)]
    private struct MftRegisterTypeInfo
    {
        public Guid Major;
        public Guid Sub;
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFTEnumEx(Guid category, uint flags, ref MftRegisterTypeInfo input, IntPtr output,
                                        out IntPtr activates, out uint count);
}
