using Emerald.Core;
using Emerald.Deltacast;
using Emerald.Video;
using System.Diagnostics;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// The IP stream, in and out: frames from a network evened out to one per tick, the clock that
/// paces them without a card, addresses written down without their keys, the ffmpeg commands, and
/// the tap on air that must never hold air up.
/// </summary>
public sealed class IpStreamTests
{
    private const int Bytes = 64;

    private static byte[] Frame(FrameRegulator r, byte mark)
    {
        byte[] f = r.Rent();
        f[0] = mark;
        return f;
    }

    private static FrameRegulator Regulator(int prebuffer = 3, int maxQueue = 6, int hold = 4) =>
        new(Bytes, prebuffer, maxQueue, hold);

    [Fact]
    public void Nothing_is_taken_until_the_store_is_filled_and_then_one_frame_a_tick_in_order()
    {
        FrameRegulator r = Regulator();
        int gen = r.Reset();

        // Before anything: black, not a frame.
        Assert.Equal(RegulatedKind.Black, r.Pull().Kind);

        r.PushVideo(gen, 0, Frame(r, 10));
        r.PushVideo(gen, 1, Frame(r, 11));
        Assert.Equal(RegulatedKind.Black, r.Pull().Kind);

        r.PushVideo(gen, 2, Frame(r, 12));

        for (byte want = 10; want <= 12; want++)
        {
            RegulatedFrame f = r.Pull();
            Assert.Equal(RegulatedKind.Fresh, f.Kind);
            Assert.Equal(want, f.Video[0]);
        }
    }

    [Fact]
    public void A_frame_late_shows_the_last_again_and_a_stream_gone_goes_to_black()
    {
        FrameRegulator r = Regulator(prebuffer: 1, hold: 4);
        int gen = r.Reset();

        r.PushVideo(gen, 0, Frame(r, 7));
        Assert.Equal(7, r.Pull().Video[0]);

        // Late: the same picture, and silence.
        RegulatedFrame again = r.Pull();
        Assert.Equal(RegulatedKind.Repeated, again.Kind);
        Assert.Equal(7, again.Video[0]);
        Assert.Null(again.Audio);

        // Still away past the hold: black.
        for (int i = 0; i < 3; i++) Assert.Equal(RegulatedKind.Repeated, r.Pull().Kind);
        RegulatedFrame black = r.Pull();
        Assert.Equal(RegulatedKind.Black, black.Kind);
        Assert.Equal(0x80, black.Video[0]);
        Assert.Equal(0x10, black.Video[1]);

        // Back: the picture again.
        r.PushVideo(gen, 1, Frame(r, 8));
        Assert.Equal(RegulatedKind.Fresh, r.Pull().Kind);
    }

    [Fact]
    public void A_rush_lets_the_oldest_go_so_the_delay_does_not_grow()
    {
        FrameRegulator r = Regulator(prebuffer: 2, maxQueue: 4);
        int gen = r.Reset();

        for (byte i = 0; i < 10; i++) r.PushVideo(gen, i, Frame(r, i));

        RegulatedFrame f = r.Pull();
        Assert.Equal(6, f.Video[0]);      // 0-5 let go; 7, 8 and 9 still waiting
        Assert.Equal(6, r.Dropped);
    }

    [Fact]
    public void Each_frame_keeps_its_own_sound_however_the_two_arrive()
    {
        FrameRegulator r = Regulator(prebuffer: 1, maxQueue: 3);
        int gen = r.Reset();

        // Sound ahead of the picture, and a rush that drops frames 0 and 1.
        for (long s = 0; s < 6; s++) r.PushAudio(gen, s, [(byte)(100 + s)]);
        for (byte i = 0; i < 5; i++) r.PushVideo(gen, i, Frame(r, i));

        RegulatedFrame f = r.Pull();
        Assert.Equal(2, f.Video[0]);
        Assert.Equal(102, f.Audio![0]);

        Assert.Equal(103, r.Pull().Audio![0]);

        // A frame whose sound has not come: silence, and the sound is not used for a later frame.
        r.PushVideo(gen, 9, Frame(r, 9));
        Assert.Equal(4, r.Pull().Video[0]);
        RegulatedFrame nine = r.Pull();
        Assert.Equal(RegulatedKind.Fresh, nine.Kind);
        Assert.Equal(9, nine.Video[0]);
        Assert.Null(nine.Audio);
    }

    [Fact]
    public void A_frame_waits_for_its_sound_rather_than_going_out_silent()
    {
        FrameRegulator r = Regulator(prebuffer: 2, maxQueue: 8);
        int gen = r.Reset();

        // Pictures 0-5 in, sound only for 0-1 so far: two frames are ready, not six.
        for (byte i = 0; i < 6; i++) r.PushVideo(gen, i, Frame(r, i));
        r.PushAudio(gen, 0, [100]);
        r.PushAudio(gen, 1, [101]);

        Assert.Equal(101 - 1, r.Pull().Audio![0]);
        Assert.Equal(101, r.Pull().Audio![0]);

        // Frame 2's sound has not come: the last picture again, and frame 2 keeps its place.
        RegulatedFrame waiting = r.Pull();
        Assert.Equal(RegulatedKind.Repeated, waiting.Kind);

        r.PushAudio(gen, 2, [102]);
        RegulatedFrame two = r.Pull();
        Assert.Equal(2, two.Video[0]);
        Assert.Equal(102, two.Audio![0]);
    }

    [Fact]
    public void A_stream_whose_sound_stops_goes_on_by_its_picture()
    {
        FrameRegulator r = Regulator(prebuffer: 1, maxQueue: 4);
        int gen = r.Reset();

        r.PushAudio(gen, 0, [1]);
        for (byte i = 0; i < 10; i++) r.PushVideo(gen, i, Frame(r, i));

        RegulatedFrame f = r.Pull();
        Assert.Equal(RegulatedKind.Fresh, f.Kind);
        Assert.Null(f.Audio);
    }

    [Fact]
    public void A_new_connection_throws_away_the_old_and_ignores_its_stragglers()
    {
        FrameRegulator r = Regulator(prebuffer: 1);
        int old = r.Reset();
        r.PushVideo(old, 0, Frame(r, 1));

        int gen = r.Reset();
        Assert.Equal(0, r.Queued);

        r.PushVideo(old, 1, Frame(r, 2));
        Assert.Equal(0, r.Queued);

        r.PushVideo(gen, 0, Frame(r, 3));
        Assert.Equal(3, r.Pull().Video[0]);
    }

    [Fact]
    public void Buffers_go_round_rather_than_being_made_every_frame()
    {
        FrameRegulator r = Regulator(prebuffer: 1);
        int gen = r.Reset();
        var seen = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);

        for (int i = 0; i < 200; i++)
        {
            byte[] f = r.Rent();
            seen.Add(f);
            r.PushVideo(gen, i, f);
            r.Pull();
        }

        Assert.True(seen.Count <= 3, $"{seen.Count} buffers made");
    }

    [Fact]
    public void The_clock_keeps_time_without_drifting()
    {
        using var clock = new FrameClock(25);
        var watch = Stopwatch.StartNew();
        double worst = 0, last = 0;

        for (int i = 0; i < 50; i++)
        {
            clock.Wait();
            double now = watch.Elapsed.TotalMilliseconds;
            worst = Math.Max(worst, now - last);
            last = now;
        }

        // Two seconds of frames in two seconds, give or take a frame, none much later than due.
        Assert.InRange(watch.Elapsed.TotalMilliseconds, 1960, 2080);
        Assert.True(worst < 80, $"a tick came {worst:F0} ms after the last");
    }

    [Theory]
    [InlineData("rtmp://live.example.com/app/SECRETKEY", "rtmp://live.example.com/app/…")]
    [InlineData("srt://10.0.0.5:8890?streamid=read:live&passphrase=hunter22", "srt://10.0.0.5:8890")]
    [InlineData("rtsp://user:pw@10.0.0.5:8554/live", "rtsp://10.0.0.5:8554/live")]
    [InlineData("twitch:somechannel", "twitch:somechannel")]
    public void An_address_is_written_down_without_its_key(string url, string shown)
    {
        Assert.Equal(shown, IpSource.Mask(url));
        Assert.DoesNotContain("SECRET", IpSource.Mask(url));
        Assert.DoesNotContain("hunter", IpSource.Mask(url));
    }

    [Theory]
    [InlineData("twitch:somechannel", "https://www.twitch.tv/somechannel")]
    [InlineData("https://www.twitch.tv/somechannel", "https://www.twitch.tv/somechannel")]
    [InlineData("twitch.tv/somechannel", "https://twitch.tv/somechannel")]
    public void A_twitch_channel_is_known_and_given_to_streamlink_as_its_page(string url, string page)
    {
        var source = new IpSource(url);
        Assert.True(source.IsTwitch);
        Assert.Equal(page, source.TwitchPage);
        Assert.False(new IpSource("srt://host:1").IsTwitch);
    }

    [Fact]
    public void The_reader_makes_the_deck_s_format_with_sound_on_the_pipe()
    {
        CaptureFormat format = CaptureFormat.ForIp(25);
        Assert.Equal(1920, format.Width);
        Assert.Equal(0u, format.Standard);   // 1080p25: what the delay and the transmitter take

        string line = string.Join(" ", IpReaderCommand.Arguments("rtsp://h:8554/live", format, "p1", withAudio: true));

        Assert.Contains("-rtsp_transport tcp", line);
        Assert.Contains("fps=fps=25:start_time=0", line);
        Assert.Contains("format=uyvy422", line);
        Assert.Contains("-f rawvideo pipe:1", line);
        Assert.Contains(@"\\.\pipe\p1", line);
        Assert.Contains("first_pts=0", line);

        string silent = string.Join(" ", IpReaderCommand.Arguments("https://h/x.m3u8", format, "p1", withAudio: false));
        Assert.Contains("-re", silent);
        Assert.DoesNotContain("pipe\\p1", silent);
    }

    [Fact]
    public void The_preview_reads_a_small_picture_with_its_sound_for_the_meters()
    {
        string line = string.Join(" ", IpReaderCommand.PreviewArguments("rtsp://h:8554/stream1", 480, 270, 12, "pv", withAudio: true));

        Assert.Contains("-rtsp_transport tcp", line);
        Assert.Contains("scale=480:270:force_original_aspect_ratio=decrease", line);
        Assert.Contains("fps=12,format=bgra", line);
        Assert.Contains("-f rawvideo pipe:1", line);
        Assert.Contains(@"\\.\pipe\pv", line);
        Assert.Contains("-y", line);

        Assert.DoesNotContain(@"pipe\pv", string.Join(" ", IpReaderCommand.PreviewArguments("rtsp://h/x", 480, 270, 12, "pv", withAudio: false)));
    }

    [Fact]
    public void Settings_changed_in_one_window_are_carried_into_another_s_copy()
    {
        var live = new AppSettings { WebStreamEnabled = true, WebStreamUrl = "rtsp://10.0.0.2:8554/onair", WebStreamBitrateKbps = 3000 };
        WebStreamSettings s = WebStreamSettings.From(live);

        Assert.True(s.Enabled);
        Assert.Equal("rtsp://10.0.0.2:8554/onair", s.Url);

        var other = new AppSettings();
        s.WriteTo(other);
        Assert.True(other.WebStreamEnabled);
        Assert.Equal(3000, other.WebStreamBitrateKbps);
    }

    [Theory]
    [InlineData(24, 8u)]
    [InlineData(30, 1u)]
    [InlineData(50, 10u)]
    [InlineData(29, 0u)]
    public void An_ip_stream_comes_in_as_a_1080_progressive_standard(int rate, uint standard)
    {
        CaptureFormat f = CaptureFormat.ForIp(rate);
        Assert.Equal(standard, f.Standard);
        Assert.False(f.IsInterlaced);
        Assert.Equal(1080, f.Height);
    }

    [Fact]
    public void The_stream_out_has_h264_aac_and_opus_and_suits_the_address()
    {
        VideoFormat f = VideoFormats.Default;

        string rtsp = string.Join(" ", WebStreamCommand.Arguments(f, "w1", "rtsp://127.0.0.1:8554/onair", 4500, null));
        Assert.Contains("-c:v libx264", rtsp);
        Assert.Contains("-bf 0", rtsp);
        Assert.Contains("-g 25", rtsp);
        Assert.Contains("-c:a:0 aac", rtsp);
        Assert.Contains("-c:a:1 libopus", rtsp);
        Assert.Contains("-f rtsp -rtsp_transport tcp rtsp://127.0.0.1:8554/onair", rtsp);

        Assert.Contains("-f flv", string.Join(" ", WebStreamCommand.Arguments(f, "w1", "rtmp://h/live/k", 4500, null)));
        Assert.Contains("-f mpegts", string.Join(" ", WebStreamCommand.Arguments(f, "w1", "srt://h:8890", 4500, null)));

        string gpu = string.Join(" ", WebStreamCommand.Arguments(f, "w1", "rtsp://h/x", 4500, ProxyEncoder.H264Nvenc));
        Assert.Contains("h264_nvenc", gpu);
        Assert.DoesNotContain("hevc", gpu);
    }

    [Fact]
    public void Sound_from_a_stream_loads_into_the_meters_as_it_would_off_the_card()
    {
        using var reader = new SdiAudioReader(SdiAudioReader.MaxChannels, 25);
        var bytes = new byte[1920 * 4];

        for (int s = 0; s < 1920; s++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(s * 4), (short)(s + 1));
            BitConverter.TryWriteBytes(bytes.AsSpan(s * 4 + 2), (short)-(s + 1));
        }

        Assert.Equal(1920, reader.Load(bytes, bytes.Length, 2));
        Assert.Equal(1, reader.PairsPresent());
        Assert.Equal(5, reader.Channels[0][4]);
        Assert.Equal(-5, reader.Channels[1][4]);

        // Interleaved for a two-pair recording: the second pair silent, in its place.
        var into = new byte[1920 * 8];
        Assert.Equal(1920 * 8, reader.Interleave(into, 4));
        Assert.Equal(5, BitConverter.ToInt16(into, 4 * 8));
        Assert.Equal(0, BitConverter.ToInt16(into, 4 * 8 + 4));
    }

    /// <summary>An air output that takes a frame every 40 ms, as a card does.</summary>
    private sealed class SlowCard(VideoFormat format) : ISdiOutput
    {
        public int Pushed;
        public VideoFormat Format => format;
        public bool PushFrame(byte[] frame, IReadOnlyList<short[]>? audioChannels = null) { Pushed++; return true; }
        public byte[] BlackFrame() => new byte[format.FrameBytes];
        public void Dispose() { }
    }

    [Fact]
    public void The_tap_hands_every_frame_to_air_and_returns_at_once_even_with_the_stream_off()
    {
        var card = new SlowCard(VideoFormats.Default);
        var tap = new WebStreamTap(card);
        var frame = new byte[VideoFormats.Default.FrameBytes];

        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++) Assert.True(tap.PushFrame(frame, [new short[1920], new short[1920]]));

        Assert.Equal(100, card.Pushed);
        Assert.True(watch.ElapsedMilliseconds < 500, $"{watch.ElapsedMilliseconds} ms for 100 frames");
        Assert.Same(card, AirOutputs.Card(tap));
    }

    [Fact]
    public void Without_a_card_air_is_paced_by_the_clock()
    {
        using var output = new SoftClockOutput(VideoFormats.Default);
        byte[] black = output.BlackFrame();

        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 26; i++) output.PushFrame(black);

        Assert.InRange(watch.Elapsed.TotalMilliseconds, 960, 1080);
        Assert.Equal(0x10, black[1]);
    }
}
