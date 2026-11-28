using Emerald.Core;
using System.Text.Json;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// The IP stream is an option: a fresh install, and every settings file from before it was
/// offered, records from the SDI card, and the IP stream of air is off.
/// </summary>
public sealed class CaptureInputSettingsTests
{
    [Fact]
    public void A_fresh_install_records_from_the_card_and_sends_no_ip_stream()
    {
        var settings = new AppSettings();

        Assert.Equal("sdi", settings.CaptureInput);
        Assert.False(settings.CaptureFromIp);
        Assert.False(settings.WebStreamEnabled);
        Assert.False(settings.AirWithoutCard);
    }

    [Fact]
    public void A_settings_file_from_before_records_from_the_card()
    {
        AppSettings old = JsonSerializer.Deserialize<AppSettings>("""{ "capturePort": "RX1" }""")!;

        Assert.False(old.CaptureFromIp);
        Assert.Equal("RX1", old.CapturePort);
    }

    [Fact]
    public void The_choice_is_kept_and_the_derived_values_are_not_written()
    {
        var settings = new AppSettings { CaptureInput = "ip", IpInputUrl = "rtsp://10.0.0.5:8554/live" };

        string json = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("CaptureFromIp", json);
        Assert.DoesNotContain("WebStreamViewers", json);

        AppSettings back = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.True(back.CaptureFromIp);
        Assert.Equal("rtsp://10.0.0.5:8554/live", back.IpInputUrl);
    }

    [Fact]
    public void Viewers_are_told_this_computer_s_address_for_a_stream_sent_to_itself()
    {
        var settings = new AppSettings { WebStreamUrl = "rtsp://127.0.0.1:8554/onair" };
        (string webRtc, string hls) = settings.WebStreamViewers;

        Assert.Equal($"http://{LocalAddress.IPv4}:8889/onair", webRtc);
        Assert.Equal($"http://{LocalAddress.IPv4}:8880/onair", hls);

        settings.WebStreamUrl = "rtsp://10.0.0.9:8554/hall";
        Assert.Equal("http://10.0.0.9:8889/hall", settings.WebStreamViewers.WebRtc);
    }
}
