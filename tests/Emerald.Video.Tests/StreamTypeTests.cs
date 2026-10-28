using Emerald.Deltacast;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// The channel-to-stream-type table, pinned against the SDK header.
///
/// <b>Why this is worth a test at all.</b> These are the numbers that decide which physical
/// connector a stream opens on. They are not a sequence — VHD_STREAMTYPE interleaves RX, TX,
/// the coupled types, the modify types and the hardware-process types, so the value after TX3
/// is not TX4 — and a wrong one here does not fail, it transmits on the wrong output. That is
/// the kind of mistake that is found by somebody watching the wrong feed go to air.
///
/// Copied from VideoMasterHD_Core.h, VHD_STREAMTYPE:
///   RX0=0 RX1=1 RX2=9 RX3=10, RX4..7=20..23, RX8..11=44..47
///   TX0=2 TX1=3 TX2=11 TX3=12, TX4..7=33..36, TX8..11=48..51
/// </summary>
public sealed class StreamTypeTests
{
    [Theory]
    [InlineData(0, 2u)]
    [InlineData(1, 3u)]
    [InlineData(2, 11u)]
    [InlineData(3, 12u)]
    [InlineData(4, 33u)]
    [InlineData(5, 34u)]
    [InlineData(6, 35u)]
    [InlineData(7, 36u)]
    [InlineData(8, 48u)]
    [InlineData(9, 49u)]
    [InlineData(10, 50u)]
    [InlineData(11, 51u)]
    public void Every_tx_channel_maps_to_the_stream_type_the_sdk_defines(int channel, uint expected) =>
        Assert.Equal(expected, VideoMasterHD.TxStreamType(channel));

    [Theory]
    [InlineData(0, 0u)]
    [InlineData(1, 1u)]
    [InlineData(2, 9u)]
    [InlineData(3, 10u)]
    [InlineData(4, 20u)]
    [InlineData(5, 21u)]
    [InlineData(6, 22u)]
    [InlineData(7, 23u)]
    [InlineData(8, 44u)]
    [InlineData(9, 45u)]
    [InlineData(10, 46u)]
    [InlineData(11, 47u)]
    public void Every_rx_channel_maps_to_the_stream_type_the_sdk_defines(int channel, uint expected) =>
        Assert.Equal(expected, VideoMasterHD.RxStreamType(channel));

    [Fact]
    public void No_two_tx_channels_share_a_stream_type()
    {
        // A duplicate would mean two ports on the deck opening the same connector, and the
        // second would fail with the channel reported as already in use.
        var seen = Enumerable.Range(0, VideoMasterHD.MaxChannels)
            .Select(VideoMasterHD.TxStreamType)
            .ToList();

        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public void No_tx_channel_shares_a_stream_type_with_an_rx_channel()
    {
        // The enum interleaves them, so a transposed digit would open an input as an output.
        var tx = Enumerable.Range(0, VideoMasterHD.MaxChannels).Select(VideoMasterHD.TxStreamType);
        var rx = Enumerable.Range(0, VideoMasterHD.MaxChannels).Select(VideoMasterHD.RxStreamType);

        Assert.Empty(tx.Intersect(rx));
    }

    [Fact]
    public void A_channel_beyond_what_can_be_named_is_refused_rather_than_guessed()
    {
        // Refusing is the point: the next number in the enum after a run of TX values belongs
        // to a coupled or hardware-process type, so inventing one would open something else.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VideoMasterHD.TxStreamType(VideoMasterHD.MaxChannels));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => VideoMasterHD.RxStreamType(VideoMasterHD.MaxChannels));

        Assert.Throws<ArgumentOutOfRangeException>(() => VideoMasterHD.TxStreamType(-1));
    }

    [Fact]
    public void Supports_agrees_with_what_can_actually_be_named()
    {
        // The deck asks Supports before offering a port, so if the two ever disagreed it would
        // offer one that throws - which is exactly the fault this replaced.
        for (int channel = -1; channel <= VideoMasterHD.MaxChannels + 1; channel++)
        {
            bool named;

            try
            {
                VideoMasterHD.TxStreamType(channel);
                named = true;
            }
            catch (ArgumentOutOfRangeException)
            {
                named = false;
            }

            Assert.Equal(named, VideoMasterHD.Supports(channel));
        }
    }

    [Fact]
    public void Seven_outputs_are_all_addressable()
    {
        // The case that prompted this: a card reporting seven TX had only four that could be
        // opened, because the table stopped at TX3 and threw beyond it.
        for (int channel = 0; channel < 7; channel++)
            Assert.True(VideoMasterHD.Supports(channel), $"TX{channel} should be addressable");
    }
}
