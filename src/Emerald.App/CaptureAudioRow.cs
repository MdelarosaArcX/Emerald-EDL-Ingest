using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Emerald.Video;

namespace Emerald.App;

/// <summary>
/// One added audio track on the capture deck: the file, what it will be called in the
/// recording, what it is being lifted or cut by, and how far it is being slid against the
/// picture.
///
/// It also carries whether it is <see cref="Airing"/> — actually in the recording that is
/// running — which is the whole answer to "how do I know this is going into the file". The
/// beds are handed to ffmpeg when the recording rolls and cannot be changed after that, so a
/// track added or adjusted mid-recording is <see cref="Pending"/> until the next one.
/// </summary>
public sealed class CaptureAudioRow : INotifyPropertyChanged
{
    /// <summary>A dB at a time, as the EDL's own track gain moves.</summary>
    public const double GainStepDb = 1;

    /// <summary>Ten milliseconds: a quarter of a frame at 25, which is about as fine as an ear reads.</summary>
    public const int OffsetStepMs = 10;

    public required string Path { get; init; }

    private string _label = "";
    private string _trackLabel = "";
    private double _gainDb;
    private int _offsetMs;
    private bool _airing;
    private bool _pending;

    /// <summary>What this track is called in the recorded file.</summary>
    public string Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    /// <summary>Which track it will be in the file, which depends on how many pairs are on the wire.</summary>
    public string TrackLabel
    {
        get => _trackLabel;
        set => Set(ref _trackLabel, value);
    }

    /// <summary>Lift or cut applied to this bed before it is written.</summary>
    public double GainDb
    {
        get => _gainDb;
        set
        {
            double clamped = Math.Clamp(Math.Round(value, 1),
                                        CaptureAudioTrack.MinGainDb, CaptureAudioTrack.MaxGainDb);

            if (!Set(ref _gainDb, clamped)) return;

            Notify(nameof(GainText));
        }
    }

    /// <summary>
    /// How far the sound is slid against the picture, positive to hold it back.
    ///
    /// The picture cannot move — it is going to air through the delay line while this is
    /// being adjusted — so the track is what moves, and a negative offset trims the front of
    /// the bed rather than advancing anything else.
    /// </summary>
    public int OffsetMs
    {
        get => _offsetMs;
        set
        {
            int clamped = Math.Clamp(value, -CaptureAudioTrack.MaxOffsetMs, CaptureAudioTrack.MaxOffsetMs);

            if (!Set(ref _offsetMs, clamped)) return;

            Notify(nameof(OffsetText));
        }
    }

    /// <summary>Always signed, so a lift reads as a lift at a glance.</summary>
    public string GainText => _gainDb == 0
        ? "0.0 dB"
        : $"{(_gainDb > 0 ? "+" : "")}{_gainDb.ToString("0.0", CultureInfo.InvariantCulture)} dB";

    public string OffsetText => _offsetMs == 0 ? "0 ms" : $"{(_offsetMs > 0 ? "+" : "")}{_offsetMs} ms";

    /// <summary>
    /// True when this track, exactly as it stands, is in the recording that is running — so
    /// every segment being written now carries it.
    /// </summary>
    public bool Airing
    {
        get => _airing;
        private set { if (Set(ref _airing, value)) Notify(nameof(StateText)); }
    }

    /// <summary>True when the recording is running but this track is not in it, or no longer matches it.</summary>
    public bool Pending
    {
        get => _pending;
        private set { if (Set(ref _pending, value)) Notify(nameof(StateText)); }
    }

    public string StateText => _airing ? "RECORDING" : _pending ? "NEXT RECORDING" : "";

    /// <summary>This track as the recorder wants it. The label falls back to the file's own name.</summary>
    public CaptureAudioTrack ToTrack() => new(
        string.IsNullOrWhiteSpace(_label) ? System.IO.Path.GetFileName(Path) : _label.Trim(),
        Path, _gainDb, _offsetMs);

    /// <summary>
    /// Sets the live state against what the running recording was actually given.
    ///
    /// Compared by value rather than by identity: an operator who nudges the gain of a track
    /// that is already recording has to be told that the nudge is not in the file, and a row
    /// that still matches what ffmpeg was handed has to keep saying so.
    /// </summary>
    public void ShowState(IReadOnlyList<CaptureAudioTrack>? airborne)
    {
        if (airborne is null)
        {
            Airing = false;
            Pending = false;
            return;
        }

        bool matched = airborne.Any(t => t == ToTrack());

        Airing = matched;
        Pending = !matched;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        Notify(name);
        return true;
    }

    private void Notify(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
