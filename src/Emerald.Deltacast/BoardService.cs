
namespace Emerald.Deltacast;

public sealed record BoardScanResult(IReadOnlyList<BoardInfo> Boards, uint ApiVersion, string? Error)
{
    public string ApiVersionString =>
        ApiVersion == 0 ? "n/a"
        : $"{(ApiVersion >> 24) & 0xFF}.{(ApiVersion >> 16) & 0xFF}.{ApiVersion & 0xFFFF}";
}

public static class BoardService
{
    /// <summary>
    /// Enumerates every DELTACAST board the local driver reports, opening each one
    /// briefly to read its RX and TX channel counts. Never throws: SDK problems come
    /// back as <see cref="BoardScanResult.Error"/> so the UI can show them inline.
    /// </summary>
    public static BoardScanResult Scan()
    {
        uint apiVersion = 0, nbBoards = 0;

        try
        {
            uint rc = VideoMasterHD.VHD_GetApiInfo(ref apiVersion, ref nbBoards);
            if (rc != VideoMasterHD.VHDERR_NOERROR)
                return new BoardScanResult(Array.Empty<BoardInfo>(), apiVersion,
                    $"VHD_GetApiInfo failed (error {rc}).");
        }
        catch (DllNotFoundException)
        {
            return new BoardScanResult(Array.Empty<BoardInfo>(), 0,
                "VideoMasterHD.dll was not found. Install the DELTACAST VideoMaster driver.");
        }
        catch (BadImageFormatException)
        {
            return new BoardScanResult(Array.Empty<BoardInfo>(), 0,
                "VideoMasterHD.dll could not be loaded. The application must run as 64-bit.");
        }
        catch (Exception ex)
        {
            return new BoardScanResult(Array.Empty<BoardInfo>(), 0, $"VideoMaster SDK error: {ex.Message}");
        }

        if (nbBoards == 0)
            return new BoardScanResult(Array.Empty<BoardInfo>(), apiVersion,
                "No DELTACAST board detected on this system.");

        var boards = new List<BoardInfo>((int)nbBoards);
        var warnings = new List<string>();

        for (uint i = 0; i < nbBoards; i++)
        {
            string model = VideoMasterHD.GetBoardModelString(i);
            uint boardType = 0, rx = 0, tx = 0;

            IntPtr handle = IntPtr.Zero;
            uint openRc = VideoMasterHD.VHD_OpenBoardHandle(i, ref handle, IntPtr.Zero, 0);
            if (openRc == VideoMasterHD.VHDERR_NOERROR)
            {
                try
                {
                    VideoMasterHD.VHD_GetBoardProperty(handle, VideoMasterHD.VHD_CORE_BP_BOARD_TYPE, ref boardType);
                    VideoMasterHD.VHD_GetBoardProperty(handle, VideoMasterHD.VHD_CORE_BP_NB_RXCHANNELS, ref rx);
                    VideoMasterHD.VHD_GetBoardProperty(handle, VideoMasterHD.VHD_CORE_BP_NB_TXCHANNELS, ref tx);
                }
                finally
                {
                    VideoMasterHD.VHD_CloseBoardHandle(handle);
                }
            }
            else
            {
                warnings.Add($"Board {i} ({model}) could not be opened (error {openRc}); it may be in use.");
            }

            boards.Add(new BoardInfo
            {
                Index = i,
                Model = model,
                BoardType = boardType,
                BoardTypeName = VideoMasterHD.BoardTypeName(boardType),
                RxCount = (int)rx,
                TxCount = (int)tx,
                RxPorts = Enumerable.Range(0, (int)rx).Select(n => new ChannelPort("RX", n)).ToList(),
                TxPorts = Enumerable.Range(0, (int)tx).Select(n => new ChannelPort("TX", n)).ToList(),
            });
        }

        return new BoardScanResult(boards, apiVersion, warnings.Count == 0 ? null : string.Join(" ", warnings));
    }

    public static Task<BoardScanResult> ScanAsync() => Task.Run(Scan);
}
