namespace Emerald.Deltacast;

/// <summary>Thrown when an RX channel is already claimed by something that will not give it up.</summary>
public sealed class RxBusyException : Exception
{
    public RxBusyException(string message) : base(message) { }
}

/// <summary>
/// Arbitrates who owns an RX channel.
///
/// The hardware allows exactly one open handle per receiver — that is why dCARE has to be
/// closed before the app can record. Emerald now has two claimants of its own: the shell's
/// live preview and the EDL's recorder, and they routinely want the same input. Without a
/// referee the second one fails with VHDERR_CHANNELUSED, which reads like a hardware fault
/// rather than a queue.
///
/// So claims are ranked. The preview takes a <b>yielding</b> lease: when the recorder asks
/// for the same channel the preview is revoked, closes its stream and steps aside. The
/// recorder takes an <b>exclusive</b> lease, which nothing may take from it. When a lease is
/// released, <see cref="Freed"/> fires so a yielded preview can come back on its own.
/// </summary>
public sealed class RxLease : IDisposable
{
    private static readonly object Gate = new();
    private static readonly Dictionary<(uint Board, int Channel), RxLease> Held = new();

    /// <summary>Raised after a lease is released, so a displaced claimant can retry.</summary>
    public static event Action<uint, int>? Freed;

    public uint Board { get; }
    public int Channel { get; }
    public string Owner { get; }

    /// <summary>True while this lease still owns the channel.</summary>
    public bool IsActive { get; private set; } = true;

    private readonly bool _yielding;
    private readonly Action? _onRevoked;

    private RxLease(uint board, int channel, string owner, bool yielding, Action? onRevoked)
    {
        Board = board;
        Channel = channel;
        Owner = owner;
        _yielding = yielding;
        _onRevoked = onRevoked;
    }

    /// <param name="yielding">
    /// True for a claim that should stand aside for a more important one — the preview.
    /// False for a claim that must not be interrupted once granted — recording.
    /// </param>
    /// <param name="onRevoked">
    /// Invoked, on the revoking thread, when a yielding lease is taken away. It must close
    /// the stream before returning, because the new owner opens the channel immediately after.
    /// </param>
    public static RxLease Acquire(uint board, int channel, string owner,
                                  bool yielding = false, Action? onRevoked = null)
    {
        RxLease? displaced = null;
        RxLease lease;

        lock (Gate)
        {
            var key = (board, channel);

            if (Held.TryGetValue(key, out RxLease? current))
            {
                if (!current._yielding)
                    throw new RxBusyException(
                        $"RX{channel} on board {board} is in use by {current.Owner}.");

                if (yielding)
                    throw new RxBusyException(
                        $"RX{channel} on board {board} is already previewing in {current.Owner}.");

                displaced = current;
                current.IsActive = false;
                Held.Remove(key);
            }

            lease = new RxLease(board, channel, owner, yielding, onRevoked);
            Held[key] = lease;
        }

        // Outside the lock: the callback closes a hardware stream and can take a moment.
        // It must finish before the new owner opens the channel, so this is not fire-and-forget.
        if (displaced is not null)
        {
            try { displaced._onRevoked?.Invoke(); } catch { /* a stuck claimant must not block the recorder */ }
        }

        return lease;
    }

    public void Dispose()
    {
        bool wasHolder;

        lock (Gate)
        {
            var key = (Board, Channel);
            wasHolder = IsActive && Held.TryGetValue(key, out RxLease? current) && ReferenceEquals(current, this);
            if (wasHolder) Held.Remove(key);
            IsActive = false;
        }

        if (wasHolder) Freed?.Invoke(Board, Channel);
    }
}
