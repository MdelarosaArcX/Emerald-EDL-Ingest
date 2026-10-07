using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Emerald.Core;

/// <summary>A request line, once it has been made sense of.</summary>
public readonly record struct MonitorRequest(string Method, string Path, string Query)
{
    /// <summary>
    /// Pulls a query value out, or null. No unescaping beyond the plus sign: everything asked
    /// for here is a number or a short word, and a decoder is a surface this does not need.
    /// </summary>
    public string? Value(string name)
    {
        foreach (string pair in Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;

            if (pair.AsSpan(0, eq).Equals(name, StringComparison.OrdinalIgnoreCase))
                return pair[(eq + 1)..].Replace('+', ' ');
        }

        return null;
    }

    public long Number(string name, long fallback) =>
        long.TryParse(Value(name), out long value) ? value : fallback;
}

/// <summary>
/// Serves the monitoring page and its data to other machines on the network.
///
/// <b>A socket rather than <see cref="HttpListener"/>.</b> That was not a preference: on
/// Windows, HttpListener refuses any prefix but localhost unless the process is elevated or
/// somebody has run <c>netsh http add urlacl</c> first — verified on this plant, access denied
/// for both the wildcard and the machine's own address. A remote monitor that needs an
/// administrator before it will listen is a remote monitor nobody turns on. A
/// <see cref="TcpListener"/> binds every interface with no privilege at all, and the HTTP a
/// read-only page needs is a request line, a few headers to ignore, and a response with a
/// length on it.
///
/// It adds no dependency. The whole solution has one NuGet package and this does not make it
/// two.
///
/// <b>Read-only, and deliberately so.</b> Only GET is answered. Nothing served can change
/// anything: no starting or stopping a recording, no taking the transmitter. A remote machine
/// watches the plant and cannot touch it.
/// </summary>
public sealed class MonitorServer : IDisposable
{
    /// <summary>Where it listens unless told otherwise.</summary>
    public const int DefaultPort = 8787;

    /// <summary>A request head longer than this is not one of ours; the connection is dropped.</summary>
    private const int MaxHeadBytes = 8 * 1024;

    /// <summary>The most of an unwanted request body that will be read off the socket and dropped.</summary>
    private const int MaxBodyBytes = 64 * 1024;

    /// <summary>How long a connection may take to send its request before it is abandoned.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly ActivityLog _log;
    private readonly PipelineState _state;
    private readonly TidalLock _tidal;
    private readonly Func<Timecode?> _clock;
    private readonly string _name;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }

    public bool Running => _listener is not null;

    /// <summary>The addresses to type on another machine, once it is listening.</summary>
    public IReadOnlyList<string> Addresses => Running
        ? MonitorFeed.Addresses(Port)
        : Array.Empty<string>();

    public MonitorServer(ActivityLog log, PipelineState state, TidalLock tidal,
                         Func<Timecode?> clock, string name)
    {
        _log = log;
        _state = state;
        _tidal = tidal;
        _clock = clock;
        _name = name;
    }

    /// <summary>
    /// Starts listening on every interface, or says why it could not.
    ///
    /// The usual reason is the port already being in use — another Emerald, or something else
    /// on the machine — and that is worth a sentence rather than an exception, because the
    /// operator is the one who has to pick a different number.
    /// </summary>
    public bool TryStart(int port, out string? problem)
    {
        problem = null;

        if (Running) return true;

        if (port is < 1 or > 65535)
        {
            problem = $"{port} is not a port number.";
            return false;
        }

        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();

            _listener = listener;
            Port = port;
            _cts = new CancellationTokenSource();

            _ = Task.Run(() => AcceptLoop(listener, _cts.Token));

            ActivityLog.Shared.Ok(LogSource.App,
                $"Remote monitoring is on at {string.Join(", ", Addresses)} - read-only, " +
                "open to anything on this network.",
                @event: "monitor.api.started",
                detail: string.Join("\n", Addresses));

            return true;
        }
        catch (SocketException ex)
        {
            problem = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"Port {port} is already in use. Pick another."
                : $"Could not listen on port {port}: {ex.Message}";

            _listener = null;
            return false;
        }
    }

    public void Stop()
    {
        if (_listener is not { } listener) return;

        _listener = null;
        _cts?.Cancel();

        try { listener.Stop(); } catch (SocketException) { }

        _cts?.Dispose();
        _cts = null;

        ActivityLog.Shared.Warn(LogSource.App, "Remote monitoring is off.",
                                @event: "monitor.api.stopped");
    }

    // ------------------------------------------------------------------ the loop

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }

            // One connection must never hold up the next, and a page that goes away mid-request
            // must not take the server with it.
            _ = Task.Run(() => Serve(client, ct), ct);
        }
    }

    private async Task Serve(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;

                using NetworkStream stream = client.GetStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ReadTimeout);

                string? head = await ReadHead(stream, timeout.Token);
                if (head is null) return;

                if (!TryParse(head, out MonitorRequest request))
                {
                    await Write(stream, 400, "text/plain", MonitorFeed.Bytes("bad request"), ct);
                    return;
                }

                // Whatever body came with the request, read and thrown away before answering.
                //
                // Nothing here wants a request body — only GET is served — but it has to be
                // taken off the socket anyway. Closing a connection with unread data still in
                // the receive buffer is an abortive close: Windows sends RST instead of FIN, and
                // the reply already written is destroyed in flight. The client sees "error while
                // copying content" rather than the 405 it was actually sent, which turns a clear
                // refusal into an unexplained network failure.
                await DrainBody(stream, head, timeout.Token);

                await Respond(stream, request, ct);

                // FIN behind the response rather than on top of it, and a moment for the stack
                // to get the last bytes out before the handle goes.
                try
                {
                    client.Client.LingerState = new LingerOption(true, 2);
                    client.Client.Shutdown(SocketShutdown.Send);
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            // Anything else is a bug here rather than a bad caller, and must not be silent —
            // but it must also not stop the server serving the next request.
            ActivityLog.Shared.Warn(LogSource.App,
                $"A remote monitoring request failed: {ex.Message}",
                @event: "monitor.api.failed");
        }
    }


    /// <summary>
    /// Reads and discards a request body, so the socket is empty when it is closed.
    ///
    /// Bounded twice over: never more than the length the request declared, and never more than
    /// a cap, because the length is a claim by the caller and a claim of a gigabyte should not
    /// become a gigabyte of reading. Anything past the cap is left, and that connection is then
    /// reset — which is the right outcome for something sending this a body that size.
    /// </summary>
    private static async Task DrainBody(NetworkStream stream, string head, CancellationToken ct)
    {
        long length = DeclaredLength(head);
        if (length <= 0) return;

        long left = Math.Min(length, MaxBodyBytes);
        byte[] bin = new byte[4096];

        while (left > 0)
        {
            int read = await stream.ReadAsync(bin.AsMemory(0, (int)Math.Min(left, bin.Length)), ct);
            if (read == 0) return;

            left -= read;
        }
    }

    /// <summary>The Content-Length a request claims, or 0 when it does not claim one.</summary>
    public static long DeclaredLength(string head)
    {
        foreach (string line in head.Split('\n'))
        {
            string trimmed = line.Trim();

            if (!trimmed.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;

            return long.TryParse(trimmed["Content-Length:".Length..].Trim(), out long length) && length > 0
                ? length
                : 0;
        }

        return 0;
    }

    /// <summary>Reads up to the blank line that ends the head, or gives up.</summary>
    private static async Task<string?> ReadHead(NetworkStream stream, CancellationToken ct)
    {
        var head = new List<byte>(512);
        byte[] one = new byte[1];

        while (head.Count < MaxHeadBytes)
        {
            int read = await stream.ReadAsync(one, ct);
            if (read == 0) return null;

            head.Add(one[0]);

            int n = head.Count;
            if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' &&
                          head[n - 2] == '\r' && head[n - 1] == '\n')
                return Encoding.ASCII.GetString(head.ToArray());

            // A bare LF pair, which some clients and every hand-typed telnet session send.
            if (n >= 2 && head[n - 2] == '\n' && head[n - 1] == '\n')
                return Encoding.ASCII.GetString(head.ToArray());
        }

        return null;
    }

    /// <summary>
    /// Makes sense of a request head, or does not. Pure and public so the routing can be
    /// checked without a socket — the part most likely to be wrong is the part that decides
    /// which page somebody gets.
    /// </summary>
    public static bool TryParse(string head, out MonitorRequest request)
    {
        request = default;

        int lineEnd = head.IndexOfAny(new[] { '\r', '\n' });
        string line = lineEnd < 0 ? head : head[..lineEnd];

        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        string target = parts[1];
        if (target.Length == 0 || target[0] != '/') return false;

        int q = target.IndexOf('?');

        request = new MonitorRequest(
            parts[0].ToUpperInvariant(),
            q < 0 ? target : target[..q],
            q < 0 ? "" : target[(q + 1)..]);

        return true;
    }

    private async Task Respond(NetworkStream stream, MonitorRequest request, CancellationToken ct)
    {
        if (request.Method is not ("GET" or "HEAD"))
        {
            await Write(stream, 405, "text/plain", MonitorFeed.Bytes("read-only"), ct);
            return;
        }

        switch (request.Path)
        {
            case "/":
            case "/index.html":
                await Write(stream, 200, "text/html; charset=utf-8",
                            MonitorFeed.Bytes(MonitorPage.Html), ct);
                return;

            case "/api/health":
                await Json(stream, MonitorFeed.Health(_name), ct);
                return;

            case "/api/state":
                await Json(stream, MonitorFeed.State(_log, _state, _tidal, _clock), ct);
                return;

            case "/api/log":
                await Json(stream, MonitorFeed.Log(_log, request.Number("since", 0),
                                                   (int)request.Number("limit", 500)), ct);
                return;

            case "/api/transmitted":
                await Json(stream, MonitorFeed.Transmitted(_log), ct);
                return;

            default:
                await Write(stream, 404, "text/plain", MonitorFeed.Bytes("no such thing here"), ct);
                return;
        }
    }

    private static Task Json(NetworkStream stream, string body, CancellationToken ct) =>
        Write(stream, 200, "application/json; charset=utf-8", MonitorFeed.Bytes(body), ct);

    /// <summary>
    /// One response, with a length on it and the connection closed after.
    ///
    /// No keep-alive: the page polls a few times a second and a fresh connection each time
    /// costs nothing on a LAN, while getting keep-alive subtly wrong costs a browser that
    /// hangs waiting for a body that already arrived.
    /// </summary>
    private static async Task Write(NetworkStream stream, int status, string type,
                                    byte[] body, CancellationToken ct)
    {
        string reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Error",
        };

        var head = new StringBuilder();
        head.Append($"HTTP/1.1 {status} {reason}\r\n");
        head.Append($"Content-Type: {type}\r\n");
        head.Append($"Content-Length: {body.Length}\r\n");
        head.Append("Cache-Control: no-store\r\n");

        // The page is served from the same origin it calls, so this is not needed for the
        // page itself. It is here so somebody's own dashboard can read the JSON.
        head.Append("Access-Control-Allow-Origin: *\r\n");
        head.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    public void Dispose() => Stop();
}
