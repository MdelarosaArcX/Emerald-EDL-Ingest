using System.IO;
using Emerald.Media;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// What the storage limit has deleted from the front of a recording. The first segment left has
/// to start where it really did, or every segment after it lands behind air by however much has
/// gone - which is how the timeline came to stop showing new segments once the store was full.
/// </summary>
public sealed class PruneLedgerTests : IDisposable
{
    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "emerald-ledger-tests", Guid.NewGuid().ToString("N"));

    public PruneLedgerTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void With_nothing_deleted_the_first_segment_starts_at_the_roll()
    {
        Assert.Null(PruneLedger.Read(_folder));
        Assert.Equal(TimeSpan.Zero, PruneLedger.StartOf(1, null, Ten));
    }

    [Fact]
    public void Each_deletion_moves_the_start_on_by_what_actually_ran()
    {
        PruneLedger.Record(_folder, 1, TimeSpan.FromSeconds(9));
        PruneLedger.Record(_folder, 2, TimeSpan.FromSeconds(11));

        PruneLedger.Entry? ledger = PruneLedger.Read(_folder);

        Assert.Equal(new PruneLedger.Entry(2, TimeSpan.FromSeconds(20)), ledger);
        Assert.Equal(TimeSpan.FromSeconds(20), PruneLedger.StartOf(3, ledger, Ten));
    }

    [Fact]
    public void The_other_half_of_a_pair_is_not_counted_twice()
    {
        PruneLedger.Record(_folder, 1, Ten);
        PruneLedger.Record(_folder, 1, Ten);

        Assert.Equal(new PruneLedger.Entry(1, Ten), PruneLedger.Read(_folder));
    }

    [Fact]
    public void A_gap_before_the_ledger_began_is_filled_at_a_typical_length()
    {
        // Segments 1 to 456 went before there was a ledger; 457 is the first written down.
        PruneLedger.Record(_folder, 457, Ten);

        Assert.Equal(Ten * 457, PruneLedger.Read(_folder)!.Value.Through);
        Assert.Equal(Ten * 457, PruneLedger.StartOf(458, PruneLedger.Read(_folder), Ten));

        // And with no ledger at all, the same answer from the count alone.
        Assert.Equal(Ten * 457, PruneLedger.StartOf(458, null, Ten));
    }
}
