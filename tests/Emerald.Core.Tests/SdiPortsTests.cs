using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// Who holds which output.
///
/// The cases worth protecting are the ones where getting it wrong shows an operator a control
/// that cannot work, or greys out one that can: a module must not be told its own ports are
/// taken, re-arming must not lose a port, and a module that stops in an unexpected path must not
/// leave an output marked busy for the rest of the session.
/// </summary>
public sealed class SdiPortsTests
{
    private static SdiPort Tx(int channel, uint board = 0) => new(board, channel);

    [Fact]
    public void A_free_port_is_held_by_nobody()
    {
        Assert.Null(new SdiPorts().HeldBy(Tx(0)));
    }

    [Fact]
    public void A_held_port_names_who_has_it()
    {
        var ports = new SdiPorts();

        Assert.True(ports.TryHold("tidal lock", Tx(1)));
        Assert.Equal("tidal lock", ports.HeldBy(Tx(1)));
    }

    [Fact]
    public void Another_owner_cannot_take_a_held_port()
    {
        var ports = new SdiPorts();

        ports.TryHold("tidal lock", Tx(1));

        Assert.False(ports.TryHold("EDL playout", Tx(1)));
        Assert.Equal("tidal lock", ports.HeldBy(Tx(1)));
    }

    [Fact]
    public void Re_claiming_what_you_already_hold_succeeds()
    {
        // Re-arming must not mean releasing first, which would open a window for something else
        // to take the port in between.
        var ports = new SdiPorts();

        Assert.True(ports.TryHold("tidal lock", Tx(1)));
        Assert.True(ports.TryHold("tidal lock", Tx(1)));
        Assert.Equal("tidal lock", ports.HeldBy(Tx(1)));
    }

    [Fact]
    public void A_fan_out_is_all_or_nothing()
    {
        var ports = new SdiPorts();

        ports.TryHold("EDL playout", Tx(2));

        // Half a fan-out is a port quietly off air, so the whole claim is refused.
        Assert.False(ports.TryHold("tidal lock", Tx(0), Tx(1), Tx(2)));

        Assert.Null(ports.HeldBy(Tx(0)));
        Assert.Null(ports.HeldBy(Tx(1)));
        Assert.Equal("EDL playout", ports.HeldBy(Tx(2)));
    }

    [Fact]
    public void Holding_nothing_is_allowed_and_changes_nothing()
    {
        var ports = new SdiPorts();

        Assert.True(ports.TryHold("nobody"));
        Assert.Empty(ports.All());
    }

    [Fact]
    public void The_same_channel_on_another_board_is_a_different_port()
    {
        var ports = new SdiPorts();

        ports.TryHold("tidal lock", Tx(1, board: 0));

        Assert.True(ports.TryHold("EDL playout", Tx(1, board: 1)));
    }

    [Fact]
    public void A_module_is_never_told_its_own_ports_are_taken()
    {
        var ports = new SdiPorts();

        ports.TryHold("tidal lock", Tx(1));

        Assert.Null(ports.HeldByOther(Tx(1), me: "tidal lock"));
        Assert.Equal("tidal lock", ports.HeldByOther(Tx(1), me: "EDL playout"));
    }

    [Fact]
    public void Releasing_gives_back_everything_that_owner_had_and_nothing_else()
    {
        var ports = new SdiPorts();

        ports.TryHold("tidal lock", Tx(0), Tx(1));
        ports.TryHold("EDL playout", Tx(2));

        ports.ReleaseAll("tidal lock");

        Assert.Null(ports.HeldBy(Tx(0)));
        Assert.Null(ports.HeldBy(Tx(1)));
        Assert.Equal("EDL playout", ports.HeldBy(Tx(2)));
    }

    [Fact]
    public void Releasing_an_owner_that_holds_nothing_is_not_an_error()
    {
        var ports = new SdiPorts();

        ports.ReleaseAll("never held anything");

        Assert.Empty(ports.All());
    }

    [Fact]
    public void A_released_port_can_be_taken_by_someone_else()
    {
        var ports = new SdiPorts();

        ports.TryHold("tidal lock", Tx(1));
        ports.ReleaseAll("tidal lock");

        Assert.True(ports.TryHold("EDL playout", Tx(1)));
    }

    [Fact]
    public void Taking_and_giving_back_both_say_so()
    {
        var ports = new SdiPorts();

        int changes = 0;
        ports.Changed += () => changes++;

        ports.TryHold("tidal lock", Tx(1));
        Assert.Equal(1, changes);

        ports.ReleaseAll("tidal lock");
        Assert.Equal(2, changes);

        // A refused claim and an empty release changed nothing, so they say nothing - a deck
        // that redrew on every failed attempt would flicker for no reason.
        ports.TryHold("a", Tx(1));
        ports.TryHold("b", Tx(1));
        Assert.Equal(3, changes);

        ports.ReleaseAll("never held anything");
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Eight_threads_claiming_one_port_produce_exactly_one_winner()
    {
        // The decks and the modules are on different threads, and two transmitters starting
        // together is exactly when this matters.
        var ports = new SdiPorts();

        int won = 0;

        Parallel.For(0, 8, i =>
        {
            if (ports.TryHold($"owner {i}", Tx(1))) Interlocked.Increment(ref won);
        });

        Assert.Equal(1, won);
        Assert.NotNull(ports.HeldBy(Tx(1)));
    }
}
