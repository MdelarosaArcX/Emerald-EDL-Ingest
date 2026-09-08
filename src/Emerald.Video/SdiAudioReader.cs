using Emerald.Deltacast;
using System.Runtime.InteropServices;

namespace Emerald.Video;

/// <summary>
/// Pulls embedded audio out of a locked SDI slot.
///
/// SDI carries four groups of four channels — sixteen mono channels, eight stereo pairs —
/// and until now the recorder asked for two of them. That was fine while a message had one
/// language; it is not fine when the EDL puts four languages to air on four pairs and the
/// return feed records only the first. So this asks for all sixteen, reports which actually
/// arrived, and lets the caller decide how many to keep.
///
/// <c>VHD_AUDIOINFO</c> is hand-built at explicit offsets rather than marshalled: the
/// struct is a fixed 1600 bytes of four 400-byte groups, and the layout here is the same one
/// already validated on the transmit side in <c>SdiOutput</c>. Zeroing the whole block leaves
/// every group and channel <c>VHD_AM_OFF</c>, so only the channels written below are asked
/// for, and a card with fewer groups simply returns nothing for the rest.
/// </summary>
public sealed class SdiAudioReader : IDisposable
{
    public const int SampleRate = 48000;
    public const int ChannelsPerGroup = 4;
    public const int Groups = 4;

    /// <summary>Sixteen mono channels, which is everything the standard carries.</summary>
    public const int MaxChannels = Groups * ChannelsPerGroup;

    /// <summary>Eight stereo pairs: channels 1-2, 3-4, and so on.</summary>
    public const int MaxPairs = MaxChannels / 2;

    private const int AudioInfoBytes = 1600;   // 4 groups x 400
    private const int GroupBytes = 400;
    private const int GroupChannelsOffset = 64;
    private const int ChannelBytes = 80;
    private const int ChannelMode = 0, ChannelFormat = 4, ChannelDataSize = 68, ChannelData = 72;

    private readonly int _wantChannels;
    private readonly int _capacityBytes;

    private IntPtr _info;
    private readonly IntPtr[] _channelBuffers;

    /// <summary>Samples per channel in the last successful read; zero when the slot had none.</summary>
    public int Samples { get; private set; }

    /// <summary>
    /// How many channels carried audio in the last read. Not every feed fills all sixteen,
    /// and a pair that arrived empty is a pair that should not be recorded or metered.
    /// </summary>
    public int ChannelsPresent { get; private set; }

    /// <summary>Whether each channel carried anything, indexed as channel 1 is 0.</summary>
    private readonly bool[] _present;

    public bool IsPresent(int channel) => channel >= 0 && channel < _wantChannels && _present[channel];

    /// <summary>
    /// De-interleaved samples, one array per channel. Reused between reads, so a caller that
    /// needs to keep them copies out before the next <see cref="Read"/>.
    /// </summary>
    public short[][] Channels { get; }

    /// <param name="wantChannels">
    /// How many mono channels to ask the card for, from channel 1 upwards. Capped at
    /// <see cref="MaxChannels"/>.
    /// </param>
    /// <param name="frameRate">Used only to size the buffers; a generous multiple is taken.</param>
    public SdiAudioReader(int wantChannels, int frameRate)
    {
        _wantChannels = Math.Clamp(wantChannels, 1, MaxChannels);

        // The SDK returns whatever the slot held, which is not guaranteed to be exactly
        // SampleRate/frameRate; four frames of headroom is cheap and removes the question.
        int samplesPerFrame = SampleRate / Math.Max(1, frameRate);
        _capacityBytes = samplesPerFrame * 2 * 4;

        _info = Marshal.AllocHGlobal(AudioInfoBytes);
        _channelBuffers = new IntPtr[_wantChannels];
        Channels = new short[_wantChannels][];
        _present = new bool[_wantChannels];

        for (int i = 0; i < _wantChannels; i++)
        {
            _channelBuffers[i] = Marshal.AllocHGlobal(_capacityBytes);
            Channels[i] = new short[_capacityBytes / 2];
        }

        _asking = _wantChannels;
    }

    /// <summary>The number of channels this reader asks for, which bounds every index above.</summary>
    public int ChannelCount => _wantChannels;

    /// <summary>
    /// How many channels the card actually accepted a request for. Starts at what was asked
    /// for and only ever comes down, when the SDK refuses the larger request.
    /// </summary>
    public int Asking => _asking;

    private int _asking;

    /// <summary>Points the request at this reader's buffers for the first N channels.</summary>
    private void SetUpChannels(int count)
    {
        // Zeroed first: everything not written below stays VHD_AM_OFF, so only the channels
        // named here are asked for.
        for (int i = 0; i < AudioInfoBytes; i += 8) Marshal.WriteInt64(_info, i, 0);

        for (int i = 0; i < count; i++)
        {
            IntPtr channel = ChannelAt(i);

            Marshal.WriteInt32(channel, ChannelMode, (int)VideoMasterHD.VHD_AM_MONO);
            Marshal.WriteInt32(channel, ChannelFormat, (int)VideoMasterHD.VHD_AF_16);
            Marshal.WriteInt32(channel, ChannelDataSize, _capacityBytes);
            Marshal.WriteIntPtr(channel, ChannelData, _channelBuffers[i]);
        }
    }

    /// <summary>Channel <c>i</c> lives in group <c>i / 4</c>, channel <c>i % 4</c> — SMPTE 299.</summary>
    private IntPtr ChannelAt(int i) =>
        _info
        + (i / ChannelsPerGroup) * GroupBytes
        + GroupChannelsOffset
        + (i % ChannelsPerGroup) * ChannelBytes;

    /// <summary>
    /// Reads one slot's audio. Returns the number of samples per channel, or zero when the
    /// slot carried none — which is normal on black between messages and must not be treated
    /// as a failure.
    /// </summary>
    public int Read(IntPtr slot)
    {
        Samples = 0;
        ChannelsPresent = 0;
        Array.Clear(_present);

        if (_info == IntPtr.Zero) return 0;

        // Asking for groups a card does not have could be refused outright rather than
        // answered with what it does have - and losing the audio on channels 1-2 because
        // nothing was on 13-16 would be far worse than not seeing the extra pairs. So a
        // refusal steps the request down and tries again, and the count that worked is
        // remembered: the first slot pays for this and no other does.
        while (true)
        {
            SetUpChannels(_asking);

            if (VideoMasterHD.VHD_SlotExtractAudio(slot, _info) == 0) break;

            if (_asking <= 2) return 0;

            _asking = _asking > ChannelsPerGroup ? _asking - ChannelsPerGroup : 2;
        }

        // Channels are read back one at a time because they do not have to agree: a feed with
        // audio on 1-2 only returns zero bytes for the rest, and that is the signal that those
        // pairs are not there rather than an error.
        int longest = 0;

        for (int i = 0; i < _asking; i++)
        {
            int bytes = Marshal.ReadInt32(ChannelAt(i), ChannelDataSize);
            if (bytes <= 0 || bytes > _capacityBytes) continue;

            int samples = bytes / 2;
            Marshal.Copy(_channelBuffers[i], Channels[i], 0, samples);

            // Beyond what this channel supplied is stale audio from the last slot, which
            // would be heard as a stutter if a longer channel made the frame longer.
            if (samples < Channels[i].Length) Array.Clear(Channels[i], samples, Channels[i].Length - samples);

            _present[i] = true;
            ChannelsPresent++;
            longest = Math.Max(longest, samples);
        }

        Samples = longest;
        return longest;
    }

    /// <summary>
    /// Interleaves the channels read into <paramref name="destination"/> as signed 16-bit
    /// little-endian, in channel order, and returns the byte count.
    ///
    /// <paramref name="channels"/> is what the encoder was told to expect, so a channel the
    /// slot did not carry still occupies its place as silence — dropping it would shift every
    /// language after it onto the wrong track.
    /// </summary>
    public int Interleave(byte[] destination, int channels)
    {
        channels = Math.Clamp(channels, 1, _wantChannels);

        int samples = Samples;
        int bytes = samples * channels * 2;

        if (samples <= 0 || bytes > destination.Length) return 0;

        for (int s = 0; s < samples; s++)
        {
            int o = s * channels * 2;

            for (int c = 0; c < channels; c++)
            {
                short v = _present[c] ? Channels[c][s] : (short)0;
                destination[o + c * 2] = (byte)(v & 0xFF);
                destination[o + c * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
        }

        return bytes;
    }

    /// <summary>
    /// How many stereo pairs actually carried audio, counting from the first — so a feed on
    /// channels 1-2 and 3-4 reports two, and one on 1-2 alone reports one.
    ///
    /// Counted from the front rather than by highest occupied pair: a gap would otherwise
    /// silently renumber the languages after it, and recording a silent pair to hold the
    /// position is the lesser of the two evils.
    /// </summary>
    public int PairsPresent()
    {
        int pairs = 0;

        for (int p = 0; p * 2 + 1 < _asking; p++)
        {
            if (!_present[p * 2] && !_present[p * 2 + 1]) break;
            pairs++;
        }

        return pairs;
    }

    public void Dispose()
    {
        for (int i = 0; i < _channelBuffers.Length; i++)
        {
            if (_channelBuffers[i] != IntPtr.Zero) Marshal.FreeHGlobal(_channelBuffers[i]);
            _channelBuffers[i] = IntPtr.Zero;
        }

        if (_info != IntPtr.Zero) Marshal.FreeHGlobal(_info);
        _info = IntPtr.Zero;
    }
}
