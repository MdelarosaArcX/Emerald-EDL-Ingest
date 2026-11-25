using Emerald.Core;

namespace Emerald.Video;

/// <summary>
/// The sound of one recording frame as it goes to air: the chosen pair out of every pair the delay
/// carries, as the two mono channels the card embeds, at the level set on that frame.
///
/// <b>The delay carries every recorded pair, interleaved</b> - four pairs is eight channels side by
/// side. Read as stereo, which it was, that is four times as many samples as there are, taken a pair
/// at a time from different tracks.
///
/// Pure and without a card, so it can be tested. One of these per transmitter: it remembers the
/// level and the track the last frame ended on, so a change is ramped or cross-faded over one frame
/// rather than made between two samples, which is a click on air.
/// </summary>
public sealed class RecordingSound
{
    private short[] _left = Array.Empty<short>();
    private short[] _right = Array.Empty<short>();
    private readonly List<short[]> _channels = new(2) { Array.Empty<short>(), Array.Empty<short>() };

    /// <summary>The level and the track the last frame ended on; no track after silence.</summary>
    private double _gain = 1;
    private int _track = -1;

    /// <summary>Silence has gone out in the recording's place: it comes back ramped up from nothing.</summary>
    public void Silenced()
    {
        _gain = 0;
        _track = -1;
    }

    /// <summary>
    /// A frame's sound, as the two channels to embed, or null when the frame has none.
    /// </summary>
    /// <param name="audio">Interleaved 16-bit samples of <paramref name="channels"/> channels.</param>
    /// <param name="track">Which pair, from nought. One the recording does not carry plays the first.</param>
    public List<short[]>? Frame(byte[] audio, int bytes, int channels, int track, double gainDb)
    {
        double from = _gain;
        double to = AudioEdits.Linear(gainDb);
        _gain = to;

        int was = _track < 0 ? track : _track;
        _track = track;

        int samples = Samples(bytes, channels);
        if (samples <= 0) return null;

        if (_left.Length != samples)
        {
            _left = new short[samples];
            _right = new short[samples];
            _channels[0] = _left;
            _channels[1] = _right;
        }

        int now = Pair(track, channels), before = Pair(was, channels);

        for (int i = 0; i < samples; i++)
        {
            double t = (i + 1) / (double)samples;
            double l = Sample(audio, channels, i, now), r = Sample(audio, channels, i, now + 1);

            // Out of one track and into the other across this frame.
            if (now != before)
            {
                l = Sample(audio, channels, i, before) * (1 - t) + l * t;
                r = Sample(audio, channels, i, before + 1) * (1 - t) + r * t;
            }

            double gain = from + (to - from) * t;
            _left[i] = Fit(l * gain);
            _right[i] = Fit(r * gain);
        }

        return _channels;
    }

    /// <summary>
    /// The chosen pair into the arrays given, as many samples as both have, at one level - the
    /// recording heard under a video-only item. Returns how many samples were filled.
    /// </summary>
    public static int Read(byte[] audio, int bytes, int channels, int track, double gainDb, short[] left, short[] right)
    {
        int samples = Math.Min(left.Length, Samples(bytes, channels));
        int pair = Pair(track, channels);
        double gain = AudioEdits.Linear(gainDb);

        for (int i = 0; i < samples; i++)
        {
            left[i] = Fit(Sample(audio, channels, i, pair) * gain);
            right[i] = Fit(Sample(audio, channels, i, pair + 1) * gain);
        }

        return samples;
    }

    private static int Samples(int bytes, int channels) => Math.Max(0, bytes) / (2 * Math.Max(2, channels));

    /// <summary>The first channel of a track, or of the first track when the recording has no such one: sound rather than silence.</summary>
    private static int Pair(int track, int channels) => track >= 0 && track * 2 + 1 < channels ? track * 2 : 0;

    private static int Sample(byte[] audio, int channels, int sample, int channel)
    {
        int o = (sample * Math.Max(2, channels) + channel) * 2;
        return (short)(audio[o] | (audio[o + 1] << 8));
    }

    private static short Fit(double value) => (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
}
