using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// Request parsing. The part that decides which page somebody on another machine gets, checked
/// without opening a socket.
/// </summary>
public class MonitorRequestTests
{
    [Fact]
    public void An_ordinary_request_is_a_method_a_path_and_a_query()
    {
        Assert.True(MonitorServer.TryParse("GET /api/log?since=42&limit=10 HTTP/1.1\r\nHost: x\r\n\r\n",
                                           out MonitorRequest r));

        Assert.Equal("GET", r.Method);
        Assert.Equal("/api/log", r.Path);
        Assert.Equal(42, r.Number("since", 0));
        Assert.Equal(10, r.Number("limit", 0));
    }

    [Fact]
    public void A_path_with_no_query_has_an_empty_one_rather_than_a_null()
    {
        Assert.True(MonitorServer.TryParse("GET / HTTP/1.1\r\n\r\n", out MonitorRequest r));

        Assert.Equal("/", r.Path);
        Assert.Equal("", r.Query);
        Assert.Null(r.Value("since"));
    }

    [Fact]
    public void A_missing_or_unreadable_number_falls_back_rather_than_throwing()
    {
        MonitorServer.TryParse("GET /api/log?since=nonsense HTTP/1.1\r\n\r\n", out MonitorRequest r);

        Assert.Equal(7, r.Number("since", 7));
        Assert.Equal(7, r.Number("absent", 7));
    }

    [Fact]
    public void The_method_is_read_whatever_case_it_arrives_in()
    {
        MonitorServer.TryParse("get / HTTP/1.1\r\n\r\n", out MonitorRequest r);

        Assert.Equal("GET", r.Method);
    }

    [Theory]
    [InlineData("")]
    [InlineData("GET\r\n")]
    [InlineData("GET api/log HTTP/1.1\r\n")]          // no leading slash
    [InlineData("nonsense\r\n\r\n")]
    public void Anything_that_is_not_a_request_is_refused(string head)
    {
        Assert.False(MonitorServer.TryParse(head, out _));
    }
}

/// <summary>
/// The server, end to end, over a real socket on the loopback.
///
/// The parsing can be checked in isolation but the thing that matters to an operator on another
/// machine is whether a browser gets a page and valid JSON, and that is not provable without
/// actually speaking HTTP to it.
/// </summary>
public class MonitorServerTests : IDisposable
{
    private readonly ActivityLog _log = new();
    private readonly PipelineState _state = new();
    private readonly TidalLock _tidal = new();
    private readonly MonitorServer _server;
    private readonly int _port;

    public MonitorServerTests()
    {
        _server = new MonitorServer(_log, _state, _tidal, () => null, "test");

        // A high port picked per run, so a test does not fail because the last one is still
        // letting go of its socket.
        _port = 24000 + Random.Shared.Next(2000);

        Assert.True(_server.TryStart(_port, out string? problem), problem);
    }

    /// <summary>Speaks HTTP at it the way a browser would, and returns the status and body.</summary>
    private (int Status, string Type, string Body) Get(string target, string method = "GET")
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", _port);

        using NetworkStream stream = client.GetStream();

        byte[] request = Encoding.ASCII.GetBytes(
            $"{method} {target} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");

        stream.Write(request);
        stream.Flush();

        using var reader = new StreamReader(stream, Encoding.UTF8);
        string all = reader.ReadToEnd();

        int split = all.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string head = split < 0 ? all : all[..split];
        string body = split < 0 ? "" : all[(split + 4)..];

        string[] lines = head.Split("\r\n");
        int status = int.Parse(lines[0].Split(' ')[1]);

        string type = lines.FirstOrDefault(l =>
            l.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase)) ?? "";

        return (status, type, body);
    }

    [Fact]
    public void It_listens_and_says_where()
    {
        Assert.True(_server.Running);
        Assert.Equal(_port, _server.Port);
        Assert.NotEmpty(_server.Addresses);
        Assert.All(_server.Addresses, a => Assert.Contains($":{_port}/", a));
    }

    [Fact]
    public void The_root_is_the_page_itself()
    {
        (int status, string type, string body) = Get("/");

        Assert.Equal(200, status);
        Assert.Contains("text/html", type);
        Assert.Contains("EMERALD MONITOR", body);
        Assert.Contains("api/state", body);
    }

    [Fact]
    public void Health_says_which_machine_answered()
    {
        (int status, _, string body) = Get("/api/health");

        Assert.Equal(200, status);

        using JsonDocument json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("test", json.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void The_state_carries_every_stage_the_strip_draws()
    {
        _state.Recording = true;
        _state.Capture = new StageState("RECORDING", "clip from board 0", LogLevel.Ok);
        _state.SetRecorded(1000, 4, 2);
        _state.SetAired(900, 1, 3, 0);

        (int status, string type, string body) = Get("/api/state");

        Assert.Equal(200, status);
        Assert.Contains("application/json", type);

        using JsonDocument json = JsonDocument.Parse(body);
        System.Text.Json.JsonElement root = json.RootElement;

        Assert.True(root.GetProperty("recording").GetBoolean());
        Assert.Equal("RECORDING", root.GetProperty("capture").GetProperty("headline").GetString());
        Assert.Equal(1000, root.GetProperty("frames").GetProperty("recorded").GetInt64());
        Assert.Equal(4, root.GetProperty("frames").GetProperty("notEncoded").GetInt64());
        Assert.Equal(900, root.GetProperty("frames").GetProperty("aired").GetInt64());
        Assert.Equal(3, root.GetProperty("frames").GetProperty("skipped").GetInt64());
        Assert.Equal("Off", root.GetProperty("delay").GetProperty("phase").GetString());
    }

    /// <summary>
    /// The cursor is the whole reason a remote page is cheap: it asks for what it has not seen
    /// and gets exactly that, so a page left open overnight catches up in one request.
    /// </summary>
    [Fact]
    public void The_log_is_served_by_cursor_with_no_repeats_and_no_gaps()
    {
        _log.Info(LogSource.Capture, "one");
        _log.Ok(LogSource.Playout, "two", @event: "playout.video", file: @"C:\m\a.mov");

        (_, _, string first) = Get("/api/log?since=0");

        using JsonDocument all = JsonDocument.Parse(first);
        Assert.Equal(2, all.RootElement.GetProperty("count").GetInt32());

        long last = all.RootElement.GetProperty("lastSeq").GetInt64();

        // Nothing new: the same cursor must come back empty rather than repeating.
        (_, _, string none) = Get($"/api/log?since={last}");
        Assert.Equal(0, JsonDocument.Parse(none).RootElement.GetProperty("count").GetInt32());

        _log.Warn(LogSource.Media, "three");

        (_, _, string next) = Get($"/api/log?since={last}");
        using JsonDocument after = JsonDocument.Parse(next);

        Assert.Equal(1, after.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("three",
            after.RootElement.GetProperty("lines")[0].GetProperty("message").GetString());
    }

    [Fact]
    public void A_line_arrives_with_everything_the_page_shows()
    {
        _log.Ok(LogSource.Playout, "ONAIR promo.mov", @event: "playout.video",
                correlation: "a1b2c3d4", file: @"C:\m\promo.mov");

        (_, _, string body) = Get("/api/log?since=0");

        System.Text.Json.JsonElement line =
            JsonDocument.Parse(body).RootElement.GetProperty("lines")[0];

        Assert.Equal("PLAYOUT", line.GetProperty("source").GetString());
        Assert.Equal("Ok", line.GetProperty("level").GetString());
        Assert.Equal("a1b2c3d4", line.GetProperty("id").GetString());
        Assert.Equal("promo.mov", line.GetProperty("file").GetString());
        Assert.Equal(@"C:\m\promo.mov", line.GetProperty("path").GetString());
        Assert.Equal("playout.video", line.GetProperty("event").GetString());
    }

    [Fact]
    public void What_went_to_air_is_served_grouped_by_message()
    {
        _log.Info(LogSource.Edl, "QUEUE promo.mov", @event: "edl.queued", correlation: "a1b2c3d4");
        _log.Ok(LogSource.Playout, "ONAIR promo.mov", @event: "playout.video",
                correlation: "a1b2c3d4", file: @"C:\m\promo.mov");
        _log.Ok(LogSource.Playout, "ONAIR en.wav", @event: "playout.audio",
                correlation: "a1b2c3d4", file: @"C:\m\en.wav",
                detail: "track 1\nlabel English\nchannels 1-2");

        (_, _, string body) = Get("/api/transmitted");

        System.Text.Json.JsonElement edl =
            JsonDocument.Parse(body).RootElement.GetProperty("edls")[0];

        Assert.Equal("a1b2c3d4", edl.GetProperty("id").GetString());

        System.Text.Json.JsonElement files = edl.GetProperty("files");
        Assert.Equal(2, files.GetArrayLength());
        Assert.Equal("VIDEO", files[0].GetProperty("kind").GetString());
        Assert.Equal("AUDIO 1", files[1].GetProperty("kind").GetString());
        Assert.Equal("English", files[1].GetProperty("label").GetString());
        Assert.Equal("AIRED", files[1].GetProperty("state").GetString());
    }

    [Fact]
    public void Anything_else_is_not_found()
    {
        Assert.Equal(404, Get("/../../secrets").Status);
        Assert.Equal(404, Get("/api/nothing").Status);
    }

    /// <summary>
    /// The point of it being read-only. A remote machine watches the plant; it must not be
    /// able to reach in and change it, and the refusal is checked rather than assumed.
    /// </summary>
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void Nothing_but_reading_is_answered(string method)
    {
        Assert.Equal(405, Get("/api/state", method).Status);
    }

    [Fact]
    public void Starting_twice_is_not_an_error_and_stopping_twice_is_not_either()
    {
        Assert.True(_server.TryStart(_port, out _));

        _server.Stop();
        _server.Stop();

        Assert.False(_server.Running);
    }

    [Fact]
    public void A_port_already_taken_is_explained_rather_than_thrown()
    {
        using var other = new MonitorServer(_log, _state, _tidal, () => null, "other");

        Assert.False(other.TryStart(_port, out string? problem));
        Assert.Contains("already in use", problem);
    }

    public void Dispose()
    {
        _server.Dispose();
        _log.Dispose();
    }
}

/// <summary>
/// The server against a real HTTP client, many times over.
///
/// The socket tests above read the response with a tolerant reader, which is exactly why they
/// passed while a browser did not: <see cref="System.Net.Http.HttpClient"/> enforces the
/// Content-Length it was promised, and the first version of this server closed its socket with
/// data still in the send buffer. That is an abortive close — the bytes are discarded — and
/// because it is a race it presented as the page loading fine and then failing once in a
/// handful of requests with no pattern. Repetition is the test.
/// </summary>
public class MonitorServerHttpTests : IDisposable
{
    private readonly ActivityLog _log = new();
    private readonly MonitorServer _server;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _root;

    public MonitorServerHttpTests()
    {
        _server = new MonitorServer(_log, new PipelineState(), new TidalLock(), () => null, "test");

        int port = 26000 + Random.Shared.Next(2000);
        Assert.True(_server.TryStart(port, out string? problem), problem);

        _root = $"http://127.0.0.1:{port}/";

        for (int i = 0; i < 40; i++) _log.Info(LogSource.Capture, $"line {i}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("api/health")]
    [InlineData("api/state")]
    [InlineData("api/log?since=0")]
    [InlineData("api/transmitted")]
    public async Task Every_route_returns_its_whole_body_every_time(string path)
    {
        // Twenty in a row. One truncation anywhere is a failure, because one truncation is
        // what an operator would see as the page going blank for no reason.
        for (int i = 0; i < 20; i++)
        {
            HttpResponseMessage response = await _http.GetAsync(_root + path);
            byte[] body = await response.Content.ReadAsByteArrayAsync();

            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.True(body.Length > 0, $"empty body on attempt {i}");

            // The promise and the delivery have to agree; that is the whole bug.
            if (response.Content.Headers.ContentLength is { } declared)
                Assert.Equal(declared, body.Length);
        }
    }

    [Fact]
    public async Task The_page_asks_for_routes_that_exist()
    {
        string page = await _http.GetStringAsync(_root);

        // Every fetch in the page, checked against the server rather than assumed to match.
        var asked = System.Text.RegularExpressions.Regex
            .Matches(page, """fetch\("([^"?]+)""")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(asked);

        foreach (string path in asked)
        {
            HttpResponseMessage response = await _http.GetAsync(_root + path);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Many_pages_at_once_are_all_served()
    {
        // Several operators watching, plus each page polling twice a second.
        var all = Enumerable.Range(0, 24).Select(_ => _http.GetStringAsync(_root + "api/state")).ToArray();

        string[] bodies = await Task.WhenAll(all);

        Assert.All(bodies, b => Assert.Contains("\"frames\"", b));
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
        _log.Dispose();
    }
}

/// <summary>
/// A refused request has to arrive as a refusal.
///
/// This is the one the raw-socket tests above could not see. They assert 405 for a POST and they
/// pass, because a socket reader takes whatever arrived before the reset. A real HTTP client does
/// not: it was closing a connection with the request body still unread, Windows answered that
/// with RST rather than FIN, and the 405 already written was destroyed on the wire. The caller
/// saw "error while copying content to a stream" — a network failure where there was a perfectly
/// clear refusal.
/// </summary>
public class MonitorServerRefusalTests : IDisposable
{
    private readonly ActivityLog _log = new();
    private readonly MonitorServer _server;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _root;

    public MonitorServerRefusalTests()
    {
        _server = new MonitorServer(_log, new PipelineState(), new TidalLock(), () => null, "test");

        int port = 28000 + Random.Shared.Next(2000);
        Assert.True(_server.TryStart(port, out string? problem), problem);

        // The machine's own network address, not the loopback.
        //
        // This matters and is not fussiness: loopback does not reset a connection closed with
        // unread data, so on 127.0.0.1 the bug these tests exist for is invisible and they pass
        // against the broken server. The real interface is where an operator on another machine
        // connects, and the only place the behaviour is real. Falls back to loopback on a machine
        // with no network, where the test is then worth less but still runs.
        _root = _server.Addresses.FirstOrDefault(a => !a.Contains("localhost", StringComparison.Ordinal))
                ?? $"http://127.0.0.1:{port}/";
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task A_write_with_a_body_is_refused_in_words_the_caller_can_read(string method)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), _root + "api/state")
        {
            Content = new StringContent("{\"recording\":false}"),
        };

        HttpResponseMessage response = await _http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("read-only", body);
    }

    /// <summary>A body far larger than anything wanted still gets an answer rather than a reset.</summary>
    [Fact]
    public async Task A_large_body_is_still_answered()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _root + "api/state")
        {
            Content = new StringContent(new string('x', 40_000)),
        };

        HttpResponseMessage response = await _http.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("read-only", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Reading after a refusal has to keep working, which is what a reset would have broken.</summary>
    [Fact]
    public async Task Reading_still_works_after_a_refusal()
    {
        await _http.PostAsync(_root + "api/state", new StringContent("x"));

        string body = await _http.GetStringAsync(_root + "api/health");

        Assert.Contains("\"ok\":true", body);
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\r\nContent-Length: 12\r\n\r\n", 12)]
    [InlineData("GET / HTTP/1.1\r\ncontent-length:  7 \r\n\r\n", 7)]
    [InlineData("GET / HTTP/1.1\r\nHost: x\r\n\r\n", 0)]
    [InlineData("GET / HTTP/1.1\r\nContent-Length: nonsense\r\n\r\n", 0)]
    [InlineData("GET / HTTP/1.1\r\nContent-Length: -5\r\n\r\n", 0)]
    public void The_declared_length_is_read_or_treated_as_nothing(string head, long expected)
    {
        Assert.Equal(expected, MonitorServer.DeclaredLength(head));
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
        _log.Dispose();
    }
}
