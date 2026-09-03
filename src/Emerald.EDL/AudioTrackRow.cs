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

    public string Summary =>
        Selection.Kind == "file"
            ? Path.GetFileName(Selection.Path)
            : $"{Selection.Files.Count} file(s)";

    /// <summary>Full paths for playout; MediaSelection stores bare names against a folder.</summary>
    public IReadOnlyList<string> FullPaths =>
        Selection.Kind == "file"
            ? new[] { Selection.Path }
            : Selection.Files.Select(n => Path.Combine(Selection.Path, n)).ToList();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
