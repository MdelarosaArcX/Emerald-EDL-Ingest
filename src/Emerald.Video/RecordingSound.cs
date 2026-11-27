using Emerald.Core;

namespace Emerald.Video;

/// <summary>
/// The sound of one recording frame, out of the delay, which carries every recorded pair
/// interleaved - four pairs is eight channels side by side.
///
/// <b>To air</b> every pair goes out on its own channels, CH 1-2 first, at the level set on the
/// frame (<see cref="All"/>): which pair is heard is chosen downstream, by the hardware, not here.
/// <b>Live Edit's preview</b> takes one pair, the one the editor is listening to (<see cref="Frame"/>).
///
/// Pure and without a card, so it can be tested. One per transmitter or preview: it remembers the
/// level and the pair the last frame ended on, so a change is ramped or cross-faded over one frame
/// rather than made between two samples, which is a click.
/// </summary>
public sealed class RecordingSound
{
    private short[] _left = Array.Empty<short>();
    private short[] _right = Array.Empty<short>();
    private readonly List<short[]> _channels = new(2) { Array.Empty<short>(), Array.Empty<short>() };

    /// <summary>The level and the track the last frame ended on; no track after silence.</summary>
    private double _gain = 1;
    private int _track = -1;

    private readonly List<short[]> _all = new(16);

    /// <summary>
    /// A frame's sound as every channel it has, one mono array each in order - what the card embeds
    /// as SDI channels 1, 2, 3 ... - at the level set on it, ramped from the last frame's. Null when
    /// the frame has none.
    /// </summary>
    public List<short[]>? All(byte[] audio, int bytes, int channels, double gainDb)
    {
        double from = _gain;
        double to = AudioEdits.Linear(gainDb);
        _gain = to;
        _track = -1;

        channels = Math.Max(2, channels);
        int samples = Samples(bytes, channels);
        if (samples <= 0) return null;

        Fit(_all, channels, samples);

        for (int i = 0; i < samples; i++)
        {
            double gain = from + (to - from) * ((i + 1) / (double)samples);

            for (int c = 0; c < channels; c++)
                _all[c][i] = Fit(Sample(audio, channels, i, c) * gain);
        }

        return _all;
    }

    /// <summary>
    /// Every channel of a frame into the arrays given, as many samples as they hold, at one level -
    /// the recording heard under a video-only item. Channels it has not got are silent.
    /// </summary>
    public static void ReadAll(byte[] audio, int bytes, int channels, double gainDb, IReadOnlyList<short[]> into)
    {
        channels = Math.Max(2, channels);
        double gain = AudioEdits.Linear(gainDb);

        for (int c = 0; c < into.Count; c++)
        {
            short[] channel = into[c];
            int samples = Math.Min(channel.Length, Samples(bytes, channels));

            Array.Clear(channel);
            if (c >= channels) continue;

            for (int i = 0; i < samples; i++) channel[i] = Fit(Sample(audio, channels, i, c) * gain);
        }
    }

    /// <summary>Makes <paramref name="list"/> exactly <paramref name="count"/> arrays of <paramref name="samples"/>.</summary>
    internal static void Fit(List<short[]> list, int count, int samples)
    {
        while (list.Count > count) list.RemoveAt(list.Count - 1);
        for (int c = 0; c < list.Count; c++) if (list[c].Length != samples) list[c] = new short[samples];
        while (list.Count < count) list.Add(new short[samples]);
    }

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
