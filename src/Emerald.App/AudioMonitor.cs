using Emerald.Video;

namespace Emerald.App;

/// <summary>
/// The deck's ears: meters for every embedded pair, and one pair sent to the speakers.
///
/// It exists because the audio arrives from two different places and the operator should not
/// have to care which. While nothing is recording, the confidence preview owns the receiver
/// and raises the slots; the moment a recording starts the preview is revoked and the
/// recorder raises them instead. Both feed this, so the meters keep moving across the
/// handover rather than freezing at whatever they last read.
///
/// Levels are taken on the thread that read the slot, which is a real-time loop — so this
/// does the arithmetic and nothing else. Drawing happens on the UI timer, which reads
/// <see cref="Level"/> whenever it likes.
/// </summary>
public sealed class AudioMonitor : IDisposable
{
    private readonly object _gate = new();

    private readonly AudioMeter[] _meters;
    private readonly AudioLevel[] _levels;

    /// <summary>
    /// Guards the level snapshot alone. Its own lock, not the one the speakers use: the UI
    /// reads these forty times a second and must never wait behind a device call.
    ///
    /// A lock rather than Volatile because a level is three fields - two doubles and a flag -
    /// and nothing makes that one atomic write. Uncontended, it costs less than the meter
    /// arithmetic that produced the value.
    /// </summary>
    private readonly object _levelGate = new();

    private SpeakerOutput? _speakers;
    private int _frameRate = 25;

    private int _monitorPair = -1;
    private double _volume = 0.7;
    private bool _muted = true;

    /// <summary>When audio was last seen at all, so a dead feed can empty the meters.</summary>
    private long _lastTicks;

    /// <summary>
    /// When each channel last carried something other than silence.
    ///
    /// Presence is held for <see cref="SignalHold"/> after that, because a language falls
    /// silent between words and a row that vanished every pause would be unusable. It is the
    /// only test available: the card returns a buffer of zeros for a channel nothing is
    /// embedded on rather than saying so, and believing it would show all eight pairs on
    /// every feed — which is exactly what the deck did on its first run.
    /// </summary>
    private readonly long[] _signalTicks;

    /// <summary>Long enough to cover a pause in speech, short enough to notice a feed change.</summary>
    private static readonly TimeSpan SignalHold = TimeSpan.FromSeconds(3);

    public AudioMonitor()
    {
        _meters = new AudioMeter[SdiAudioReader.MaxChannels];
        _levels = new AudioLevel[SdiAudioReader.MaxChannels];
        _signalTicks = new long[SdiAudioReader.MaxChannels];

        for (int i = 0; i < _meters.Length; i++)
        {
            _meters[i] = new AudioMeter();
            _levels[i] = AudioLevel.Absent;
        }
    }

    /// <summary>How many stereo pairs were on the wire in the last slot seen.</summary>
    public int PairsPresent { get; private set; }

    /// <summary>Which pair goes to the speakers, or -1 for none. Muting does not change it.</summary>
    public int MonitorPair
    {
        get { lock (_gate) return _monitorPair; }
    }

    public bool IsMuted
    {
        get { lock (_gate) return _muted; }
    }

    /// <summary>Whether a sound device opened. False means meters work and the speakers do not.</summary>
    public bool CanListen
    {
        get { lock (_gate) return _speakers is { IsOpen: true }; }
    }

    /// <summary>Why the speakers are unavailable, for the log. Null when they are fine.</summary>
    public string? ListenProblem
    {
        get { lock (_gate) return _speakers?.Problem; }
    }

    /// <summary>
    /// Pairs carrying a language, counted from the first. Held over
    /// <see cref="SignalHold"/>, so a pause in speech does not drop a row. Caller holds
    /// <see cref="_levelGate"/>.
    /// </summary>
    private int CountPairs(long now, long holdTicks)
    {
        int pairs = 0;

        for (int p = 0; p * 2 + 1 < _signalTicks.Length; p++)
        {
            bool live = Live(p * 2) || Live(p * 2 + 1);
            if (!live) break;

            pairs++;
        }

        return pairs;

        bool Live(int c) => _signalTicks[c] != 0 && now - _signalTicks[c] <= holdTicks;
    }

    /// <summary>A snapshot of every channel's level, safe to read from the UI thread.</summary>
    public AudioLevel Level(int channel)
    {
        if (channel < 0 || channel >= _levels.Length) return AudioLevel.Absent;

        lock (_levelGate) return _levels[channel];
    }

    /// <summary>The louder half of a pair, which is what a single bar per pair should show.</summary>
    public AudioLevel PairLevel(int pair)
    {
        AudioLevel left = Level(pair * 2);
        AudioLevel right = Level(pair * 2 + 1);

        if (!left.Present && !right.Present) return AudioLevel.Absent;

        return left.PeakDb >= right.PeakDb ? left : right;
    }

    /// <summary>
    /// Sends a pair to the speakers. Passing -1, or the pair already selected, is how the
    /// operator stops listening; the meters are unaffected either way.
    /// </summary>
    public void Listen(int pair, int frameRate)
    {
        lock (_gate)
        {
            _frameRate = frameRate > 0 ? frameRate : _frameRate;

            if (pair < 0)
            {
                _monitorPair = -1;
                _speakers?.Flush();
                return;
            }

            _speakers ??= SpeakerOutput.Open(_frameRate);

            // Whatever was queued belongs to the pair being left, and playing it out after
            // the switch would be a moment of the wrong language.
            if (_monitorPair != pair) _speakers.Flush();

            _monitorPair = pair;
            _muted = false;
            _speakers.SetVolume(_volume);
        }
    }

    public void Mute(bool muted)
    {
        lock (_gate)
        {
            _muted = muted;
            if (muted) _speakers?.Flush();
        }
    }

    /// <summary>Monitor volume, 0 to 1. Affects the speakers only — never the recording.</summary>
    public double Volume
    {
        get { lock (_gate) return _volume; }
        set
        {
            lock (_gate)
            {
                _volume = Math.Clamp(value, 0, 1);
                _speakers?.SetVolume(_volume);
            }
        }
    }

    /// <summary>
    /// One slot's audio, from whichever of the preview or the recorder is holding the
    /// receiver. Called on their thread, so it must stay cheap and must never throw.
    /// </summary>
    public void Push(SdiAudioReader reader)
    {
        int samples = reader.Samples;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long holdTicks = (long)(SignalHold.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);

        Volatile.Write(ref _lastTicks, now);

        // One acquire for all sixteen channels, not sixteen. The meters themselves are behind
        // it too: the UI thread lets them decay when audio stops arriving, and they were
        // never built to be touched from two threads at once.
        lock (_levelGate)
        {
            for (int c = 0; c < _meters.Length && c < reader.ChannelCount; c++)
            {
                if (reader.HasSignal(c)) _signalTicks[c] = now;

                // Present means "this channel is carrying a language", not "the card handed
                // back a buffer" - which it does for all sixteen regardless.
                bool present = _signalTicks[c] != 0 && now - _signalTicks[c] <= holdTicks;

                _meters[c].Push(reader.Channels[c], present ? samples : 0, present);
                _levels[c] = _meters[c].Level;
            }

            PairsPresent = CountPairs(now, holdTicks);
        }

        int pair;
        bool muted;
        SpeakerOutput? speakers;

        lock (_gate)
        {
            pair = _monitorPair;
            muted = _muted;
            speakers = _speakers;
        }

        if (speakers is null || muted || pair < 0 || samples <= 0) return;

        int left = pair * 2;
        int right = left + 1;

        if (right >= reader.ChannelCount || (!reader.IsPresent(left) && !reader.IsPresent(right))) return;

        // Gain of 1: the monitor's volume is the device's business, so what is heard can be
        // turned down without the samples being touched.
        speakers.Push(reader.Channels[left], reader.Channels[right], samples);
    }

    /// <summary>
    /// Called from the UI timer. Lets the meters fall when no audio is arriving — a lost
    /// signal would otherwise leave every bar frozen where it was, which looks exactly like a
    /// signal that is still there.
    /// </summary>
    public void Tick(TimeSpan elapsed)
    {
        long last = Volatile.Read(ref _lastTicks);
        long since = System.Diagnostics.Stopwatch.GetTimestamp() - last;

        // A quarter second with nothing arriving is a feed that has gone, not a slow frame.
        if (last != 0 && since <= System.Diagnostics.Stopwatch.Frequency / 4) return;

        PairsPresent = 0;

        lock (_levelGate)
        {
            Array.Clear(_signalTicks);

            for (int c = 0; c < _meters.Length; c++)
            {
                _meters[c].Idle(elapsed);
                _levels[c] = _meters[c].Level;
            }
        }
    }

    public void Reset()
    {
        foreach (AudioMeter meter in _meters) meter.Reset();
        lock (_levelGate)
            for (int i = 0; i < _levels.Length; i++) _levels[i] = AudioLevel.Absent;

        PairsPresent = 0;
        _lastTicks = 0;

        lock (_levelGate) Array.Clear(_signalTicks);
        lock (_gate) _speakers?.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _speakers?.Dispose();
            _speakers = null;
        }
    }
}
