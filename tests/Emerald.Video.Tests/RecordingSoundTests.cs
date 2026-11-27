using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// The recording's sound: every pair as it goes to air, and the one pair Live Edit's editor hears.
/// Every channel here is a different steady level, so a
/// sample taken from the wrong channel, or the wrong moment, is a wrong number.
/// </summary>
public class RecordingSoundTests
{
    private const int Samples = 1920;

    private static short Level(int channel) => (short)(1000 * (channel + 1));

    private static byte[] Interleaved(int channels)
    {
        var bytes = new byte[Samples * channels * 2];

        for (int i = 0; i < Samples; i++)
            for (int c = 0; c < channels; c++)
                BitConverter.TryWriteBytes(bytes.AsSpan((i * channels + c) * 2), Level(c));

        return bytes;
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(8, 0)]
    [InlineData(8, 1)]
    [InlineData(8, 2)]
    [InlineData(8, 3)]
    [InlineData(16, 7)]
    public void The_chosen_pair_and_only_it_goes_out_at_its_own_length(int channels, int track)
    {
        byte[] audio = Interleaved(channels);

        List<short[]> sent = new RecordingSound().Frame(audio, audio.Length, channels, track, 0)!;

        Assert.Equal(2, sent.Count);
        Assert.Equal(Samples, sent[0].Length);
        Assert.All(sent[0], s => Assert.Equal(Level(track * 2), s));
        Assert.All(sent[1], s => Assert.Equal(Level(track * 2 + 1), s));
    }

    [Fact]
    public void A_track_the_recording_does_not_carry_plays_the_first_rather_than_nothing()
    {
        byte[] audio = Interleaved(2);
        Assert.All(new RecordingSound().Frame(audio, audio.Length, 2, 3, 0)![0], s => Assert.Equal(Level(0), s));
    }

    [Fact]
    public void A_change_of_track_is_cross_faded_over_one_frame()
    {
        byte[] audio = Interleaved(8);
        var sound = new RecordingSound();

        sound.Frame(audio, audio.Length, 8, 0, 0);
        short[] changing = sound.Frame(audio, audio.Length, 8, 3, 0)![0].ToArray();

        Assert.InRange(changing[0], Level(0), Level(0) + 10);
        Assert.Equal(Level(6), changing[^1]);
        for (int i = 1; i < changing.Length; i++) Assert.InRange(changing[i] - changing[i - 1], 0, 5);

        Assert.All(sound.Frame(audio, audio.Length, 8, 3, 0)![0], s => Assert.Equal(Level(6), s));
    }

    [Fact]
    public void A_change_of_level_is_ramped_and_the_recording_comes_back_from_silence_ramped()
    {
        byte[] audio = Interleaved(4);
        var sound = new RecordingSound();

        sound.Frame(audio, audio.Length, 4, 1, 0);
        short[] muting = sound.Frame(audio, audio.Length, 4, 1, double.NegativeInfinity)![0].ToArray();
        Assert.InRange(muting[0], Level(2) - 5, Level(2));
        Assert.Equal(0, muting[^1]);

        sound.Frame(audio, audio.Length, 4, 1, 0);
        sound.Silenced();

        // From nothing, and from the track chosen meanwhile - not out of the one before.
        short[] back = sound.Frame(audio, audio.Length, 4, 0, 0)![0].ToArray();
        Assert.InRange(back[0], 0, 2);
        Assert.Equal(Level(0), back[^1]);
    }

    [Fact]
    public void A_frame_without_sound_embeds_none()
    {
        Assert.Null(new RecordingSound().Frame(new byte[100], 0, 8, 0, 0));
    }

    [Fact]
    public void Under_a_video_only_item_the_chosen_pair_is_heard()
    {
        byte[] audio = Interleaved(8);
        var left = new short[Samples];
        var right = new short[Samples];

        Assert.Equal(Samples, RecordingSound.Read(audio, audio.Length, 8, 2, 0, left, right));
        Assert.All(left, s => Assert.Equal(Level(4), s));
        Assert.All(right, s => Assert.Equal(Level(5), s));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(16)]
    public void To_air_every_pair_goes_out_on_its_own_channels_in_order(int channels)
    {
        byte[] audio = Interleaved(channels);

        List<short[]> sent = new RecordingSound().All(audio, audio.Length, channels, 0)!;

        Assert.Equal(channels, sent.Count);
        for (int c = 0; c < channels; c++)
        {
            Assert.Equal(Samples, sent[c].Length);
            Assert.All(sent[c], s => Assert.Equal(Level(c), s));
        }
    }

    [Fact]
    public void To_air_a_level_applies_to_every_pair_and_is_ramped()
    {
        byte[] audio = Interleaved(8);
        var sound = new RecordingSound();

        sound.All(audio, audio.Length, 8, 0);
        List<short[]> sent = sound.All(audio, audio.Length, 8, -6.0206)!;

        for (int c = 0; c < 8; c++)
        {
            Assert.InRange(sent[c][0], Level(c) - 20, Level(c));
            Assert.InRange(sent[c][^1], Level(c) / 2 - 1, Level(c) / 2 + 1);
        }

        sent = sound.All(audio, audio.Length, 8, -6.0206)!;
        Assert.All(sent[5], s => Assert.InRange(s, Level(5) / 2 - 1, Level(5) / 2 + 1));
    }

    [Fact]
    public void Under_a_video_only_item_every_pair_of_the_recording_is_heard()
    {
        byte[] audio = Interleaved(4);
        var into = Enumerable.Range(0, 8).Select(_ => new short[Samples]).ToList();

        RecordingSound.ReadAll(audio, audio.Length, 4, 0, into);

        for (int c = 0; c < 4; c++) Assert.All(into[c], s => Assert.Equal(Level(c), s));
        for (int c = 4; c < 8; c++) Assert.All(into[c], s => Assert.Equal(0, s));
    }
}
