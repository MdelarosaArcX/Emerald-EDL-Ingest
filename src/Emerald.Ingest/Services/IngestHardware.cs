using Emerald.Deltacast;

namespace Emerald.Ingest;

/// <summary>What a scan of the receivers found.</summary>
public sealed record IngestHardwareScan(
    IReadOnlyList<BoardInfo> Boards,
    string ApiVersion,
    string? Error,
    bool Mock)
{
    /// <summary>The subtitle under the module title: "VideoMaster SDK 6.36.1 - 2 board(s) detected".</summary>
    public string Summary =>
        $"VideoMaster SDK {ApiVersion} - {Boards.Count} board(s) detected{(Mock ? "  |  SIMULATED HARDWARE" : "")}";

    /// <summary>Boards that can actually receive. A TX-only board is not an ingest board.</summary>
    public IReadOnlyList<BoardInfo> CaptureBoards => Boards.Where(b => b.RxCount > 0).ToList();
}

/// <summary>
/// Where the ingest controller finds out what receivers exist.
///
/// This is a seam, not a second SDK wrapper: the real implementation is a handful of lines
/// over <see cref="BoardService"/>, which is still the only thing in Emerald that talks to
/// VideoMaster. The seam exists so the queue, the scheduler and the whole UI can be run on
/// a machine with no card in it.
/// </summary>
public interface IIngestHardware
{
    bool IsMock { get; }
    Task<IngestHardwareScan> ScanAsync();
}

/// <summary>The real thing, straight through to Emerald.Deltacast.</summary>
public sealed class DeltacastIngestHardware : IIngestHardware
{
    public bool IsMock => false;

    public async Task<IngestHardwareScan> ScanAsync()
    {
        BoardScanResult result = await BoardService.ScanAsync().ConfigureAwait(false);
        return new IngestHardwareScan(result.Boards, result.ApiVersionString, result.Error, Mock: false);
    }
}

/// <summary>
/// Two boards that are not there.
///
/// The models and channel counts are the ones Emerald is actually deployed against, so the
/// port lists an operator sees in mock mode are the shape of the real ones. Every job
/// recorded through this is stamped <see cref="IngestJob.Mock"/>, and the UI says so out
/// loud, because a simulated recording that looked like a real one would be the single
/// most dangerous thing in this module.
/// </summary>
public sealed class MockIngestHardware : IIngestHardware
{
    public bool IsMock => true;

    public Task<IngestHardwareScan> ScanAsync()
    {
        var boards = new[]
        {
            Fake(0, "DELTA-3G-elp-h-22", "DELTA-3G", rx: 2, tx: 2),
            Fake(1, "DELTA-12G-elp-h-20", "DELTA-12G", rx: 4, tx: 0),
        };

        return Task.FromResult(new IngestHardwareScan(
            boards, ApiVersion: "6.36.1 (simulated)", Error: null, Mock: true));
    }

    private static BoardInfo Fake(uint index, string model, string typeName, int rx, int tx) => new()
    {
        Index = index,
        Model = model,
        BoardType = 0,
        BoardTypeName = typeName,
        RxCount = rx,
        TxCount = tx,
        RxPorts = Enumerable.Range(0, rx).Select(n => new ChannelPort("RX", n)).ToList(),
        TxPorts = Enumerable.Range(0, tx).Select(n => new ChannelPort("TX", n)).ToList(),
    };
}
