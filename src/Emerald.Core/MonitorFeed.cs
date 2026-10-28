using System.Text;
using System.Text.Json;

namespace Emerald.Core;

/// <summary>
/// What a remote monitor is told, as JSON.
///
/// Separate from the server that carries it, and pure apart from reading the singletons: the
/// shape of the payload is the contract a page on another machine depends on, and it is worth
/// being able to check that shape without opening a socket.
///
/// Read-only throughout. Nothing here can start, stop or change anything — a remote monitor
/// watches the plant, and taking a transmitter or a recording off the air from another machine
/// is a decision nobody should be able to make by typing a URL.
/// </summary>
public static class MonitorFeed
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>What the strip across the top draws: every stage, the delay, and the frame counts.</summary>
    public static string State(ActivityLog log, PipelineState state, TidalLock tidal, Func<Timecode?> clock)
    {
        FrameLedger frames = state.Frames;
        TidalLockState phase = tidal.State;

        var payload = new
        {
            at = DateTimeOffset.Now,
            timecode = clock()?.ToString(),
            lastSeq = log.LastSeq,
            dropped = log.Dropped,
            recording = state.Recording,

            capture = Stage(state.Capture),
            edl = Stage(state.Edl),
            onAir = Stage(state.OnAir),
            ingest = Stage(state.Ingest),

            delay = new
            {
                phase = phase.ToString(),
                detail = tidal.Detail,
                behind = TidalLock.Describe(tidal.Delay),
                fill = tidal.FillFraction,
                framesToAir = tidal.FramesToAir,
                targetFrames = tidal.TargetFrames,
            },

            frames = new
            {
                recorded = frames.Recorded,
                notEncoded = frames.NotEncoded,
                notDelayed = frames.NotDelayed,
                aired = frames.Aired,
                repeated = frames.Repeated,
                skipped = frames.Skipped,
                resyncs = frames.Resyncs,
            },
        };

        return JsonSerializer.Serialize(payload, Json);
    }

    private static object Stage(StageState stage) => new
    {
        headline = stage.Headline,
        detail = stage.Detail,
        level = stage.Level.ToString(),
        idle = stage == StageState.Idle,
    };

    /// <summary>
    /// Lines after a cursor. The remote sends back the last sequence it holds and gets what it
    /// has missed, so a page left open overnight catches up in one request rather than
    /// re-reading the whole ring on every poll.
    /// </summary>
    public static string Log(ActivityLog log, long since, int limit)
    {
        LogLine[] lines = log.Since(since, Math.Clamp(limit, 1, 2000));

        var payload = new
        {
            lastSeq = log.LastSeq,
            count = lines.Length,
            lines = lines.Select(l => new
            {
                seq = l.Seq,
                at = l.At,
                time = l.Time,
                timecode = l.Timecode,
                source = l.SourceText,
                level = l.Level.ToString(),
                message = l.Message,
                @event = l.Event,
                id = l.Correlation,
                file = l.FileText,
                path = l.File,
                detail = l.Detail,
            }),
        };

        return JsonSerializer.Serialize(payload, Json);
    }

    /// <summary>
    /// What has gone to air, per message — the same fold the master's own page shows, built
    /// from the ring here rather than sent row by row.
    /// </summary>
    public static string Transmitted(ActivityLog log)
    {
        var tally = new TransmissionTally();
        foreach (LogLine line in log.Snapshot()) tally.Add(line);

        var payload = new
        {
            edls = tally.Rows().Select(r => new
            {
                id = r.Edl.EdlId,
                headline = r.Edl.Headline,
                files = r.Files.Select(f => new
                {
                    kind = f.Kind,
                    track = f.Track,
                    label = f.Label,
                    channels = f.Channels,
                    name = f.FileName,
                    path = f.Path,
                    state = f.State,
                    times = f.Times,
                    timesText = f.TimesText,
                    lastAired = f.LastAiredText,
                    queued = f.Queued,
                }),
            }),
        };

        return JsonSerializer.Serialize(payload, Json);
    }

    /// <summary>Enough to know the master is there and which one it is, before anything else.</summary>
    public static string Health(string name)
    {
        var payload = new
        {
            ok = true,
            name,
            started = Process(),
            version = 1,
        };

        return JsonSerializer.Serialize(payload, Json);
    }

    private static DateTimeOffset Process()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().StartTime; }
        catch { return DateTimeOffset.Now; }
    }

    /// <summary>
    /// The addresses a remote should type, for the master to show on screen.
    ///
    /// Every IPv4 address the machine has that is not loopback and not a link-local
    /// autoconfiguration one — an operator on another PC needs to be told the address, and
    /// "localhost" is the one answer that cannot help them.
    /// </summary>
    public static IReadOnlyList<string> Addresses(int port)
    {
        var found = new List<string>();

        try
        {
            foreach (System.Net.NetworkInformation.NetworkInterface nic in
                     System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                foreach (System.Net.NetworkInformation.UnicastIPAddressInformation ip in
                         nic.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;

                    string text = ip.Address.ToString();
                    if (text.StartsWith("169.254", StringComparison.Ordinal)) continue;

                    found.Add($"http://{text}:{port}/");
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        catch (PlatformNotSupportedException) { }

        if (found.Count == 0) found.Add($"http://localhost:{port}/");

        return found;
    }


    /// <summary>
    /// The one command that lets another machine through, for the operator to run once as an
    /// administrator.
    ///
    /// Windows blocks inbound connections by default, so the first remote machine to try gets
    /// nothing and no explanation — the server is listening perfectly well and the packets
    /// never arrive. Emerald cannot add the rule itself without being elevated, and asking for
    /// elevation to run a capture deck would be worse. So it says what to run instead.
    /// </summary>
    public static string FirewallCommand(int port) =>
        $"netsh advfirewall firewall add rule name=\"Emerald monitoring {port}\" " +
        $"dir=in action=allow protocol=TCP localport={port}";

    /// <summary>UTF-8 without a BOM: a browser reading a BOM in JSON is a parse error.</summary>
    public static byte[] Bytes(string text) => new UTF8Encoding(false).GetBytes(text);
}
