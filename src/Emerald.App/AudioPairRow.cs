using System.ComponentModel;
using System.Runtime.CompilerServices;
using Emerald.Video;

namespace Emerald.App;

/// <summary>
/// One stereo pair on the deck's Audio Tracks panel: two meters, a name, and whether it is
/// the pair going to the speakers.
///
/// Notifies rather than being rebuilt, because these are redrawn on every UI tick and
/// replacing the collection forty times a second would fight the operator for the radio
/// button they are trying to click.
/// </summary>
public sealed class AudioPairRow : INotifyPropertyChanged
{
    public required int Pair { get; init; }

    /// <summary>"Stereo 1", as the desk labels it — counting from one.</summary>
    public string Name => $"Stereo {Pair + 1}";

    /// <summary>The SDI channels it occupies, which is what is patched downstream.</summary>
    public string ChannelLabel => $"CH {Pair * 2 + 1}-{Pair * 2 + 2}";

    private double _left, _right, _leftPeak, _rightPeak;
    private bool _present, _listening, _hot, _clipping, _audible, _recorded;
    private string _readout = "";

    /// <summary>Bar length, 0 to 1, on a -60..0 dBFS scale.</summary>
    public double Left { get => _left; private set => Set(ref _left, value); }
    public double Right { get => _right; private set => Set(ref _right, value); }

    /// <summary>Where the held peak marker sits, on the same scale.</summary>
    public double LeftPeak { get => _leftPeak; private set => Set(ref _leftPeak, value); }
    public double RightPeak { get => _rightPeak; private set => Set(ref _rightPeak, value); }

    /// <summary>
    /// Whether this pair is on the wire at all.
    ///
    /// A pair with no signal is still listed and still selectable — the operator asked to see
    /// everything the receiver can carry, and picking a pair before its language starts is a
    /// reasonable thing to want. It is drawn dim and says so rather than being hidden, because
    /// "absent" and "present and silent" are different facts and a panel that shows neither
    /// cannot tell them apart.
    /// </summary>
    public bool Present { get => _present; private set { if (Set(ref _present, value)) Notify(nameof(StatusText)); } }

    /// <summary>The pair currently going to the speakers.</summary>
    public bool Listening
    {
        get => _listening;
        set { if (Set(ref _listening, value)) Notify(nameof(StatusText)); }
    }

    /// <summary>True while the speakers are actually carrying it, rather than merely selected.</summary>
    public bool Audible
    {
        get => _audible;
        private set { if (Set(ref _audible, value)) Notify(nameof(StatusText)); }
    }

    /// <summary>
    /// True when this pair is being written into the recording that is running.
    ///
    /// Not the same as being on the wire. The channel count is fixed when the encoder starts
    /// and cannot change under it, so a pair that arrived after the recording rolled is
    /// audible on the deck and absent from the file — which is exactly the confusion this
    /// exists to prevent.
    /// </summary>
    public bool Recorded
    {
        get => _recorded;
        private set { if (Set(ref _recorded, value)) Notify(nameof(StatusText)); }
    }

    /// <summary>
    /// What this pair is doing, in one phrase: the answer to "which one am I hearing".
    ///
    /// Listening beats recording in the wording because it is the thing the operator is
    /// changing; the REC badge beside it carries the other fact at the same time.
    /// </summary>
    public string StatusText =>
        _audible && _present ? "LISTENING"

        // Selected and the speakers are on, but there is nothing on these channels. Saying
        // only LISTENING here would have the operator hunting a fault in the speakers when the
        // pair they picked is simply not being carried.
        : _audible ? "LISTENING - silent"

        : _listening ? "selected"
        : !_present ? "no signal"
        : "";

    /// <summary>The badge for a pair that is in the file, shown whether it is being heard or not.</summary>
    public string RecordedText => _recorded ? "REC" : "";

    /// <summary>Past -10 dBFS. Meters go amber, as they do on a desk.</summary>
    public bool Hot { get => _hot; private set => Set(ref _hot, value); }

    /// <summary>At full scale. Red, because from here it is being clipped.</summary>
    public bool Clipping { get => _clipping; private set => Set(ref _clipping, value); }

    /// <summary>The number under the bars: peak in dBFS, or a dash when nothing is there.</summary>
    public string Readout { get => _readout; private set => Set(ref _readout, value); }

    /// <summary>
    /// Takes the levels for this pair from the monitor and updates what is drawn.
    ///
    /// <paramref name="listening"/> is whether the speakers are on at all and
    /// <paramref name="pairsRecording"/> how many pairs the running recording is writing — both
    /// facts the row cannot see for itself, and both what the status on it is made of.
    /// </summary>
    public void Update(AudioMonitor monitor, bool listening = false, int pairsRecording = 0)
    {
        Audible = listening && _listening;
        Recorded = pairsRecording > 0 && Pair < pairsRecording;

        AudioLevel left = monitor.Level(Pair * 2);
        AudioLevel right = monitor.Level(Pair * 2 + 1);

        Present = left.Present || right.Present;

        Left = left.PeakFraction;
        Right = right.PeakFraction;

        AudioLevel loudest = left.PeakDb >= right.PeakDb ? left : right;

        Hot = Present && loudest.IsHot;
        Clipping = Present && loudest.IsClipping;

        Readout = !Present ? "-"
                : loudest.PeakDb <= AudioLevel.Silence ? "silent"
                : $"{loudest.PeakDb,5:0.0} dB";
    }

    /// <summary>Empties the bars, for when the signal has gone entirely.</summary>
    public void Clear()
    {
        Left = Right = LeftPeak = RightPeak = 0;
        Present = Hot = Clipping = false;
        Audible = Recorded = false;
        Readout = "-";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Returns whether anything changed, so a dependent property can be announced too.</summary>
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        Notify(name);
        return true;
    }

    private void Notify(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
