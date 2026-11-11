using Emerald.Core;
using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// What a recording may cost the machine, and what happens when its encoder stops.
///
/// <b>Why the thread counts are pinned.</b> ffmpeg's defaults follow the number of CPUs, so nothing
/// looks wrong on a small machine. On this plant's forty logical CPUs the same command held 3.7 GB
/// and ran 188 threads, and losing one of these flags brings that back.
/// </summary>
public sealed class EncoderResourceTests
{
    private static List<string> Args() => new RecordingProfile(0, 192, 48000, 30)
        .EncoderArguments(new CaptureFormat("1080p25", 1920, 1080, 25), "pipe", @"C:\out", "clip", 48000, singleFile: true)
        .ToList();

    /// <summary>The flags given between one -c:v and the next output's -map.</summary>
    private static List<string> OutputOptions(string codec)
    {
        List<string> args = Args();
        int from = args.IndexOf(codec);
        int to = args.IndexOf("-map", from);

        return args.GetRange(from, (to < 0 ? args.Count : to) - from);
    }

    private static string? After(List<string> args, string flag)
    {
        int at = args.IndexOf(flag);
        return at >= 0 && at + 1 < args.Count ? args[at + 1] : null;
    }

    private static readonly CaptureFormat Hd25 = new("1080p25", 1920, 1080, 25);

    private static List<string> AllArgs(ProxyEncoder encoder, int bitrate = 0) =>
        new RecordingProfile(bitrate, 192, 48000, 30, GpuProxy: true, GpuEncoder: encoder)
            .EncoderArguments(Hd25, "pipe", @"C:\out", "clip", 48000, singleFile: true)
            .ToList();

    private static List<string> GpuOutput(ProxyEncoder encoder, int bitrate = 0)
    {
        List<string> args = AllArgs(encoder, bitrate);
        int from = args.IndexOf(encoder.Codec);

        return args.GetRange(from, args.IndexOf("-map", from) - from);
    }

    public static IEnumerable<object[]> AllEncoders() => new[]
    {
        new object[] { ProxyEncoder.HevcNvenc }, new object[] { ProxyEncoder.H264Nvenc },
        new object[] { ProxyEncoder.HevcQsv }, new object[] { ProxyEncoder.H264Qsv },
    };

    [Theory]
    [MemberData(nameof(AllEncoders))]
    public void Every_gpu_proxy_is_seekable_the_same_way_as_the_cpu_one(ProxyEncoder encoder)
    {
        List<string> gpu = GpuOutput(encoder);

        Assert.Equal("0", After(gpu, "-bf"));
        Assert.Equal("12", After(gpu, "-g"));
        Assert.Equal(encoder.PixelFormat, After(gpu, "-pix_fmt"));
    }

    [Theory]
    [MemberData(nameof(AllEncoders))]
    public void No_gpu_proxy_takes_the_cpu_encoders_options(ProxyEncoder encoder)
    {
        List<string> gpu = GpuOutput(encoder);

        Assert.DoesNotContain("-threads", gpu);
        Assert.DoesNotContain("-sc_threshold", gpu);
        Assert.DoesNotContain("veryfast", gpu);
    }

    [Theory]
    [MemberData(nameof(AllEncoders))]
    public void The_master_is_on_the_cpu_whatever_the_proxy_is_on(ProxyEncoder encoder)
    {
        List<string> args = AllArgs(encoder);

        Assert.Contains("dnxhd", args);
        Assert.DoesNotContain("libx264", args);
        Assert.Equal(1, args.Count(a => a == encoder.Codec));
    }

    [Theory]
    [MemberData(nameof(AllEncoders))]
    public void A_chosen_bitrate_replaces_the_quality_setting(ProxyEncoder encoder)
    {
        List<string> gpu = GpuOutput(encoder, 5000);

        Assert.Equal("5000k", After(gpu, "-b:v"));
        Assert.Equal("5000k", After(gpu, "-maxrate"));
        Assert.Null(After(gpu, "-cq"));
        Assert.Null(After(gpu, "-global_quality"));
    }

    [Fact]
    public void Hevc_is_tagged_so_windows_players_will_open_it()
    {
        Assert.Equal("hvc1", After(GpuOutput(ProxyEncoder.HevcNvenc), "-tag:v"));
        Assert.Equal("hvc1", After(GpuOutput(ProxyEncoder.HevcQsv), "-tag:v"));
        Assert.Null(After(GpuOutput(ProxyEncoder.H264Nvenc), "-tag:v"));
    }

    [Fact]
    public void Nothing_chosen_means_x264_even_when_the_gpu_was_asked_for()
    {
        List<string> args = new RecordingProfile(0, 192, 48000, 30, GpuProxy: true)
            .EncoderArguments(Hd25, "pipe", @"C:\out", "clip", 48000, singleFile: true)
            .ToList();

        Assert.Contains("libx264", args);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3060", GpuVendor.Nvidia)]
    [InlineData("NVIDIA Quadro P2200", GpuVendor.Nvidia)]
    [InlineData("Intel(R) UHD Graphics 630", GpuVendor.Intel)]
    [InlineData("Intel(R) Arc(TM) A380 Graphics", GpuVendor.Intel)]
    [InlineData("Microsoft Basic Display Adapter", null)]
    [InlineData("AMD Radeon RX 6600", null)]
    public void A_card_is_known_by_its_name(string adapter, GpuVendor? vendor)
    {
        Assert.Equal(vendor, GpuEncoder.VendorOf(adapter));
    }

    [Fact]
    public void Hevc_comes_first_on_each_card_when_windows_can_play_it()
    {
        Assert.Equal(new[] { ProxyEncoder.HevcNvenc, ProxyEncoder.H264Nvenc },
                     GpuEncoder.Candidates(new[] { GpuVendor.Nvidia }, hevcPlays: true));
        Assert.Equal(new[] { ProxyEncoder.HevcQsv, ProxyEncoder.H264Qsv },
                     GpuEncoder.Candidates(new[] { GpuVendor.Intel }, hevcPlays: true));
    }

    [Fact]
    public void Hevc_is_never_tried_when_windows_cannot_play_it()
    {
        // Measured: it records perfectly, and then no viewer in the application will open it.
        Assert.DoesNotContain(GpuEncoder.Candidates(new[] { GpuVendor.Nvidia, GpuVendor.Intel }, hevcPlays: false),
                              e => e.Hevc);
    }

    [Fact]
    public void Nvidia_is_tried_before_intel_on_a_machine_with_both()
    {
        IReadOnlyList<ProxyEncoder> list = GpuEncoder.Candidates(
            new[] { GpuVendor.Intel, GpuVendor.Nvidia, GpuVendor.Intel }, hevcPlays: false);

        Assert.Equal(new[] { ProxyEncoder.H264Nvenc, ProxyEncoder.H264Qsv }, list);
    }

    [Fact]
    public void No_usable_card_means_nothing_to_try()
    {
        Assert.Empty(GpuEncoder.Candidates(Array.Empty<GpuVendor>(), hevcPlays: true));
    }

    [Fact]
    public void Auto_is_the_default_and_the_choice_survives_the_settings()
    {
        var settings = new AppSettings();
        Assert.True(RecordingProfile.From(settings).GpuProxy);

        (RecordingProfile.From(settings) with { GpuProxy = false }).ApplyTo(settings);
        Assert.False(RecordingProfile.From(settings).GpuProxy);
    }

    [Fact]
    public void Letting_go_of_a_ring_deletes_it_without_complaint()
    {
        string folder = Path.Combine(Path.GetTempPath(), "emerald-ring-" + Guid.NewGuid().ToString("N"));

        try
        {
            var format = new CaptureFormat("1080p25", 1920, 1080, 25);
            var line = DelayLine.Create(folder, format, TimeSpan.FromSeconds(1));
            var frame = new byte[format.FrameBytes];

            line.Write(frame, frame.Length, null, 0, false);

            string path = line.Path;
            Assert.True(File.Exists(path));

            line.Release();

            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void An_ffmpeg_that_cannot_be_started_is_a_reason_and_not_an_exception()
    {
        string? why = GpuEncoder.WhyNot(@"C:\no\such\ffmpeg.exe", ProxyEncoder.H264Nvenc, Hd25);

        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    public void The_reason_given_is_the_drivers_and_without_the_address()
    {
        string why = GpuEncoder.Reason(new[]
        {
            "[h264_nvenc @ 000001661a960bc0] Driver does not support the required nvenc API version. Required: 13.1 Found: 12.2",
            "[h264_nvenc @ 000001661a960bc0] The minimum required Nvidia driver for nvenc is 610.00 or newer",
            "Conversion failed!",
        });

        Assert.Equal("The minimum required Nvidia driver for nvenc is 610.00 or newer", why);
        Assert.Equal("ffmpeg gave no reason.", GpuEncoder.Reason(Array.Empty<string>()));
    }

    [Fact]
    public void The_proxy_encoder_has_a_thread_limit()
    {
        Assert.Equal("6", After(OutputOptions("libx264"), "-threads"));
    }

    [Fact]
    public void The_master_encoder_uses_slice_threads_and_has_a_limit()
    {
        // Frame threads are what cost the memory. The file is the same either way.
        List<string> master = OutputOptions("dnxhd");

        Assert.Equal("8", After(master, "-threads"));
        Assert.Equal("slice", After(master, "-thread_type"));
    }

    [Fact]
    public void Slice_threads_are_not_asked_of_the_proxy()
    {
        Assert.Null(After(OutputOptions("libx264"), "-thread_type"));
    }

    [Fact]
    public void The_filters_have_a_thread_limit_ahead_of_the_inputs()
    {
        List<string> args = Args();

        Assert.Equal("2", After(args, "-filter_threads"));
        Assert.True(args.IndexOf("-filter_threads") < args.IndexOf("-i"));
    }

    // ------------------------------------------------------------------ a stalled encoder

    private const int Rate = 25;
    private const int StallFrames = Rate * EncoderWatch.StallSeconds;

    private static EncoderState Refuse(EncoderWatch watch, int frames)
    {
        EncoderState last = EncoderState.Running;
        for (int i = 0; i < frames; i++) last = watch.Note(false);
        return last;
    }

    [Fact]
    public void A_stall_is_said_when_the_time_is_up_and_not_before()
    {
        var watch = new EncoderWatch(Rate);

        Assert.Equal(EncoderState.Running, Refuse(watch, StallFrames - 1));
        Assert.Equal(EncoderState.Stalled, watch.Note(false));
    }

    [Fact]
    public void A_stall_is_said_once()
    {
        var watch = new EncoderWatch(Rate);
        Refuse(watch, StallFrames);

        Assert.Equal(EncoderState.Running, Refuse(watch, StallFrames * 3));
    }

    [Fact]
    public void Scattered_drops_are_not_a_stall()
    {
        // An encoder that is merely behind drops frames and takes frames. The count starts again
        // with every frame it takes, however many it has dropped in all.
        var watch = new EncoderWatch(Rate);

        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(EncoderState.Running, Refuse(watch, StallFrames - 1));
            Assert.Equal(EncoderState.Running, watch.Note(true));
        }
    }

    [Fact]
    public void Recovery_is_said_once_and_a_second_stall_is_said_again()
    {
        var watch = new EncoderWatch(Rate);
        Refuse(watch, StallFrames);

        Assert.Equal(EncoderState.Recovered, watch.Note(true));
        Assert.Equal(EncoderState.Running, watch.Note(true));
        Assert.Equal(EncoderState.Stalled, Refuse(watch, StallFrames));
    }

    [Fact]
    public void A_low_disk_is_said_once_until_the_space_comes_back()
    {
        var watch = new EncoderWatch(Rate);
        long low = EncoderWatch.LowDiskBytes - 1;

        Assert.False(watch.NoteDiskSpace(EncoderWatch.LowDiskBytes));
        Assert.True(watch.NoteDiskSpace(low));
        Assert.False(watch.NoteDiskSpace(low));

        Assert.False(watch.NoteDiskSpace(EncoderWatch.LowDiskBytes));
        Assert.True(watch.NoteDiskSpace(low));
    }
}
