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
    private bool _present, _listening, _hot, _clipping;
    private string _readout = "";

    /// <summary>Bar length, 0 to 1, on a -60..0 dBFS scale.</summary>
    public double Left { get => _left; private set => Set(ref _left, value); }
    public double Right { get => _right; private set => Set(ref _right, value); }

    /// <summary>Where the held peak marker sits, on the same scale.</summary>
    public double LeftPeak { get => _leftPeak; private set => Set(ref _leftPeak, value); }
    public double RightPeak { get => _rightPeak; private set => Set(ref _rightPeak, value); }

    /// <summary>
    /// Whether this pair is on the wire at all. A pair that is absent is greyed out and
    /// cannot be selected — which is different from one that is present and silent.
    /// </summary>
    public bool Present { get => _present; private set => Set(ref _present, value); }

    /// <summary>The pair currently going to the speakers.</summary>
    public bool Listening { get => _listening; set => Set(ref _listening, value); }

    /// <summary>Past -10 dBFS. Meters go amber, as they do on a desk.</summary>
    public bool Hot { get => _hot; private set => Set(ref _hot, value); }

    /// <summary>At full scale. Red, because from here it is being clipped.</summary>
    public bool Clipping { get => _clipping; private set => Set(ref _clipping, value); }

    /// <summary>The number under the bars: peak in dBFS, or a dash when nothing is there.</summary>
    public string Readout { get => _readout; private set => Set(ref _readout, value); }

    /// <summary>Takes the levels for this pair from the monitor and updates what is drawn.</summary>
    public void Update(AudioMonitor monitor)
    {
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
        Readout = "-";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
