namespace Emerald.Core;

/// <summary>One output of one board, which is the thing two modules can fight over.</summary>
public readonly record struct SdiPort(uint Board, int Channel)
{
    public override string ToString() => $"board {Board} TX{Channel}";
}

/// <summary>
/// Who is using which SDI output.
///
/// A DELTACAST channel takes one owner. The second module to open the same TX gets an error from
/// the driver, and the operator gets it as "the transmitter would not start" some seconds after
/// pressing the thing that was going to break — by which time the useful information, that the
/// EDL already has that port, is three layers down inside a return code.
///
/// So the modules say what they hold, and the decks can grey out a port instead of offering a
/// choice that cannot work. <b>This is a courtesy, not a lock.</b> It does not stop anything: the
/// driver is the authority on whether a channel can be opened, and a port held here may still
/// fail to open for a reason nothing in this process knows about. It exists so the UI can be
/// honest before the attempt, not so the attempt can be skipped.
/// </summary>
public sealed class SdiPorts
{
    /// <summary>The one every module and deck in the process talks to.</summary>
    public static readonly SdiPorts Shared = new();

    private readonly object _gate = new();
    private readonly Dictionary<SdiPort, string> _held = new();

    /// <summary>Raised whenever a port is taken or given back, so a deck can re-draw.</summary>
    public event Action? Changed;

    /// <summary>
    /// Claims a set of ports for one owner, all or nothing.
    ///
    /// All or nothing because a transmitter fanning one picture to three outputs either has all
    /// three or should not start: half a fan-out is a port quietly off air. Re-claiming what you
    /// already hold succeeds, so a module that re-arms does not have to release first and risk
    /// losing the port to something else in between.
    /// </summary>
    public bool TryHold(string owner, params SdiPort[] ports)
    {
        if (ports.Length == 0) return true;

        lock (_gate)
        {
            foreach (SdiPort port in ports)
                if (_held.TryGetValue(port, out string? who) && who != owner)
                    return false;

            foreach (SdiPort port in ports) _held[port] = owner;
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Gives back everything an owner holds.
    ///
    /// By owner rather than by port, because the thing that goes wrong is a module stopping in a
    /// path nobody thought about and leaving a port marked busy forever. Releasing everything it
    /// had needs no bookkeeping at the call site.
    /// </summary>
    public void ReleaseAll(string owner)
    {
        bool any;

        lock (_gate)
        {
            List<SdiPort> mine = _held.Where(p => p.Value == owner).Select(p => p.Key).ToList();

            foreach (SdiPort port in mine) _held.Remove(port);

            any = mine.Count > 0;
        }

        if (any) Changed?.Invoke();
    }

    /// <summary>Who holds a port, or null. </summary>
    public string? HeldBy(SdiPort port)
    {
        lock (_gate) return _held.GetValueOrDefault(port);
    }

    /// <summary>
    /// Who holds a port, ignoring one owner — which is the question a deck actually asks.
    ///
    /// A deck must not grey out the ports its own transmitter is using: those are the ones it is
    /// transmitting on, and showing them as taken by somebody else would be a lie about its own
    /// state.
    /// </summary>
    public string? HeldByOther(SdiPort port, string me)
    {
        string? who = HeldBy(port);

        return who == me ? null : who;
    }

    /// <summary>Everything held, for a log line or a diagnostic.</summary>
    public IReadOnlyDictionary<SdiPort, string> All()
    {
        lock (_gate) return new Dictionary<SdiPort, string>(_held);
    }
}
