using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Emerald.Deltacast;

namespace Emerald.App;

/// <summary>
/// One TX port of the selected board, and whether the delayed feed goes out of it.
///
/// Every port is listed rather than one being chosen, because feeding a transmitter and a
/// monitoring chain the same delayed picture is the ordinary case and picking one from a list
/// made it look like a choice between them. Switching a port on while something is on air does
/// not take effect until the feed is re-armed — the ports are opened when the transmitter starts
/// and a card does not grow another output under a running one — so the row says which it is
/// rather than letting the operator assume.
/// </summary>
public sealed class TxPortRow : INotifyPropertyChanged
{
    public required ChannelPort Port { get; init; }

    /// <summary>The channel index the card knows it by.</summary>
    public int Index => Port.Index;

    public string Name => Port.Name;

    public string ToggleText => $"TX{Port.Index}";

    /// <summary>ON or OFF in words, so the row reads as a state and not only as a pressed button.</summary>
    public string OnOffText => _on ? "ON" : "OFF";

    public string Hint =>
        _heldBy is { } who
            ? $"{Port.Name} is in use by {who}. A DELTACAST output takes one owner, so it cannot " +
              "carry this feed as well until that stops."
            : $"{Port.Name} - switch on to carry the delayed feed. Takes effect when the feed is armed.";

    private bool _on;
    private bool _airing;

    /// <summary>Whether the operator wants this port carrying the feed.</summary>
    public bool On
    {
        get => _on;
        set
        {
            if (!Set(ref _on, value)) return;

            Notify(nameof(OnOffText));
            Notify(nameof(StateText));
        }
    }

    /// <summary>
    /// Whether this port is actually open and carrying it right now.
    ///
    /// Not the same as <see cref="On"/>, and the difference is the whole reason this is here: a
    /// port switched on mid-transmission is wanted but not yet carrying anything.
    /// </summary>
    public bool Airing
    {
        get => _airing;
        private set
        {
            if (!Set(ref _airing, value)) return;

            Notify(nameof(StateText));
            Notify(nameof(StateBrush));
        }
    }

    private string? _heldBy;

    /// <summary>
    /// Who else is using this output, or null when it is free.
    ///
    /// Set from <see cref="Emerald.Core.SdiPorts"/>, which the EDL's playout and the delay
    /// transmitter both write to. A port another module holds cannot be opened a second time —
    /// the driver would refuse — so offering it as a choice offers a failure, and the row says
    /// who has it instead.
    /// </summary>
    public string? HeldBy
    {
        get => _heldBy;
        private set
        {
            if (!Set(ref _heldBy, value)) return;

            Notify(nameof(StateText));
            Notify(nameof(StateBrush));
            Notify(nameof(CanToggle));
            Notify(nameof(Hint));
        }
    }

    /// <summary>
    /// Whether the operator may switch this port.
    ///
    /// Off for a port somebody else holds. Deliberately still on for one this deck's own
    /// transmitter is using: that choice is remembered for the next arm, and the row already
    /// says the change does not reach what is currently on air.
    /// </summary>
    public bool CanToggle => _heldBy is null;

    public string StateText =>
        _heldBy is { } who ? $"in use - {who}"
        : _airing ? "ON AIR"
        : _on ? "armed"
        : "";

    public Brush StateBrush =>
        (Brush)System.Windows.Application.Current.Resources[
            _heldBy is not null ? "IpDim" : _airing ? "Ok" : "IpMuted"];

    /// <summary>Marks the row against the ports the running transmitter actually opened.</summary>
    public void ShowAiring(IReadOnlyCollection<int>? open) => Airing = open?.Contains(Index) == true;

    /// <summary>Marks the row against what another module is holding.</summary>
    public void ShowHeldBy(string? owner) => HeldBy = owner;

    public event PropertyChangedEventHandler? PropertyChanged;

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
