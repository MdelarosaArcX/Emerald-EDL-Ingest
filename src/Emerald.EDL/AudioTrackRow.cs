using Emerald.Core;
using Emerald.Deltacast;
using Emerald.Video;
using Emerald.Media;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Emerald.Edl;

/// <summary>
/// One language in the Audio Tracks panel. Notifies so the offset readout and the on-air
/// indicator update live while a message is playing.
/// </summary>
public sealed class AudioTrackRow : INotifyPropertyChanged
{
    public const int StepMs = 10;
    public const int LimitMs = 500;

    private string _label = "";
    private int _offsetMs;
    private bool _isDefault;
    private bool _isOnAir;

    public required MediaSelection Selection { get; init; }

    /// <summary>
    /// Which audio stream of <see cref="Selection"/> this track is, counted among the audio
    /// streams alone - so 0 is the first language embedded in the clip, 1 the second.
    ///
    /// -1 means the file has one track and it is whichever ffmpeg picks, which is what a
    /// standalone .wav bed wants. Anything else came out of the selected media itself.
    /// </summary>
    public int SourceStream { get; init; } = -1;

    /// <summary>True when this track lives inside the message's own media rather than beside it.</summary>
    public bool IsEmbedded => SourceStream >= 0;

    /// <summary>What the stream is, technically - shown under the label so two can be told apart.</summary>
    public string StreamDetail { get; init; } = "";

    private int _index;

    /// <summary>Position in the list, which is also the engine's track index.</summary>
    public int Index
    {
        get => _index;
        set { _index = value; Notify(); Notify(nameof(ChannelLabel)); }
    }

    /// <summary>
    /// The SDI channel pair this language is embedded on — track 0 on channels 1-2, track 1
    /// on 3-4, and so on. Every track is transmitted at once, so this is what an operator
    /// patches against downstream, and it is worth having on screen rather than counted out.
    /// </summary>
    public string ChannelLabel => $"CH {_index * 2 + 1}-{_index * 2 + 2}";

    public string Label
    {
        get => _label;
        set { _label = value; Notify(); }
    }

    public int OffsetMs
    {
        get => _offsetMs;
        set
        {
            _offsetMs = Math.Clamp(value, -LimitMs, LimitMs);
            Notify();
            Notify(nameof(OffsetText));
            Notify(nameof(CanDecrease));
            Notify(nameof(CanIncrease));
        }
    }

    public string OffsetText => $"{_offsetMs:+#;-#;0} ms";
    public bool CanDecrease => _offsetMs > -LimitMs;
    public bool CanIncrease => _offsetMs < LimitMs;

    // ------------------------------------------------------------------ level

    /// <summary>One press of + or -, in decibels. A dB at a time is how a desk trims.</summary>
    public const double GainStepDb = 1.0;

    private double _gainDb;
    private bool _muted;
    private double _meter;
    private string _peakText = "-";

    /// <summary>
    /// This language's level, in decibels, applied on the way to the card. 0 is the file as
    /// it is. Unlike the deck's monitor volume, this is what goes to air.
    /// </summary>
    public double GainDb
    {
        get => _gainDb;
        set
        {
            _gainDb = Math.Clamp(value, PlayoutService.MinGainDb, PlayoutService.MaxGainDb);
            Notify();
            Notify(nameof(GainText));
            Notify(nameof(CanTurnDown));
            Notify(nameof(CanTurnUp));
            Notify(nameof(IsBoosted));
        }
    }

    public string GainText => _gainDb <= PlayoutService.MinGainDb ? "-inf" : $"{_gainDb:+0.#;-0.#;0} dB";

    public bool CanTurnDown => _gainDb > PlayoutService.MinGainDb;
    public bool CanTurnUp => _gainDb < PlayoutService.MaxGainDb;

    /// <summary>Turned up past the file's own level, which is worth showing as a deliberate act.</summary>
    public bool IsBoosted => _gainDb > 0;

    /// <summary>
    /// Muted on air. Solo works by muting every other track, so this is what both buttons
    /// end up setting.
    /// </summary>
    public bool Muted
    {
        get => _muted;
        set { _muted = value; Notify(); Notify(nameof(MuteText)); }
    }

    public string MuteText => _muted ? "MUTED" : "mute";

    /// <summary>Bar length, 0 to 1, on a -60..0 dBFS scale — the deck's meters use the same one.</summary>
    public double Meter
    {
        get => _meter;
        private set { _meter = value; Notify(); }
    }

    /// <summary>The number beside the bar: peak in dBFS while playing, a dash otherwise.</summary>
    public string PeakText
    {
        get => _peakText;
        private set { _peakText = value; Notify(); }
    }

    private bool _hot, _clipping;

    /// <summary>Past -10 dBFS. Amber, as on a desk.</summary>
    public bool Hot { get => _hot; private set { _hot = value; Notify(); } }

    /// <summary>At full scale, which after a boost is a real possibility. Red.</summary>
    public bool Clipping { get => _clipping; private set { _clipping = value; Notify(); } }

    /// <summary>
    /// Takes this track's live peak off the engine. Called on the UI timer while a message is
    /// on air; <paramref name="playing"/> false empties the bar rather than leaving it frozen
    /// where the last frame left it.
    /// </summary>
    public void UpdateMeter(PlayoutService? playout, bool playing)
    {
        if (playout is null || !playing)
        {
            Meter = 0;
            PeakText = "-";
            Hot = Clipping = false;
            return;
        }

        double db = playout.GetTrackPeakDb(Index);

        Meter = Math.Clamp((db - PlayoutService.SilenceDb) / -PlayoutService.SilenceDb, 0, 1);
        PeakText = db <= PlayoutService.SilenceDb ? "silent" : $"{db,5:0.0} dB";
        Hot = db >= -10.0;
        Clipping = db >= -0.5;
    }

    /// <summary>The track that goes on air when the message starts.</summary>
    public bool IsDefault
    {
        get => _isDefault;
        set { _isDefault = value; Notify(); }
    }

    /// <summary>The track actually on air right now, which can be switched mid-message.</summary>
    public bool IsOnAir
    {
        get => _isOnAir;
        set { _isOnAir = value; Notify(); Notify(nameof(OnAirText)); }
    }

    public string OnAirText => _isOnAir ? "ON AIR" : "take";

    public string Source => Selection.Path;

    public string Summary
    {
        get
        {
            string source = Selection.Kind == "file"
                ? Path.GetFileName(Selection.Path)
                : $"{Selection.Files.Count} file(s)";

            return StreamDetail.Length > 0 ? $"{source}  -  {StreamDetail}" : source;
        }
    }

    /// <summary>Full paths for playout; MediaSelection stores bare names against a folder.</summary>
    public IReadOnlyList<string> FullPaths =>
        Selection.Kind == "file"
            ? new[] { Selection.Path }
            : Selection.Files.Select(n => Path.Combine(Selection.Path, n)).ToList();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
