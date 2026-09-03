using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emerald.Core;

public sealed class TimecodeApiResponse
{
    [JsonPropertyName("timecode")] public string? Timecode { get; set; }
    [JsonPropertyName("localTime")] public string? LocalTime { get; set; }
    [JsonPropertyName("frameRate")] public double FrameRate { get; set; }
    [JsonPropertyName("timecodeType")] public string? TimecodeType { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("serverRole")] public string? ServerRole { get; set; }
    [JsonPropertyName("timeSource")] public string? TimeSource { get; set; }
    [JsonPropertyName("sourceStatus")] public string? SourceStatus { get; set; }
    [JsonPropertyName("serverIp")] public string? ServerIp { get; set; }
    [JsonPropertyName("connectedReaders")] public int ConnectedReaders { get; set; }
    [JsonPropertyName("timestampUtc")] public DateTimeOffset? TimestampUtc { get; set; }
}

public enum TimecodeLinkState { Idle, Connecting, Online, Offline }

/// <summary>
/// Keeps a local clock locked to the timecode server. The HTTP endpoint is polled a
/// couple of times a second; between polls the timecode is free-wheeled from a
/// monotonic stopwatch so the on-screen counter advances every frame instead of
/// stepping.
///
/// A poll does not simply overwrite the baseline. Each sample arrives having spent an
/// unknown few milliseconds in the network, so consecutive samples disagree with the
/// free-wheeled count by a frame or two in either direction — and re-seating on every one
/// of them makes the frame field visibly stutter and step backwards. Instead a small
/// disagreement is <b>slewed</b> out, at most a frame per poll, and only a real difference
/// snaps. <see cref="TryGetCurrent"/> then holds the count rather than ever handing back a
/// frame it has already given out, so the display only ever counts up.
/// </summary>
public sealed class TimecodeService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How far the server may be from the free-wheeled count before it is treated as a
    /// different time rather than as jitter. A quarter of a second of frames: wide enough
    /// to cover network variance, far short of an operator re-cueing the master.
    /// </summary>
    private const double SlewToleranceSeconds = 0.25;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    private long _baseFrames;
    private long _baseTimestamp;
    private int _rate;
    private bool _haveBaseline;

    // The last count handed out, so free-wheeling and slewing together can never walk it
    // backwards. Cleared whenever the clock legitimately jumps.
    private long _lastIssued = -1;

    public string Url { get; private set; } = "";
    public TimecodeLinkState State { get; private set; } = TimecodeLinkState.Idle;
    public string? LastError { get; private set; }
    public TimecodeApiResponse? LastResponse { get; private set; }

    /// <summary>Nominal integer frame rate reported by the server; 0 until the first successful poll.</summary>
    public int FrameRate { get { lock (_gate) return _rate; } }

    public void Start(string url)
    {
        Stop();
        Url = url;
        State = TimecodeLinkState.Connecting;
        LastError = null;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        lock (_gate) { _haveBaseline = false; _lastIssued = -1; }
        State = TimecodeLinkState.Idle;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                var payload = await _http.GetFromJsonSafeAsync(Url, ct).ConfigureAwait(false);
                if (payload is null || string.IsNullOrWhiteSpace(payload.Timecode))
                    throw new InvalidOperationException("Malformed response from timecode API.");

                int rate = (int)Math.Round(payload.FrameRate);
                if (rate <= 0) rate = 25;

                if (!Timecode.TryParse(payload.Timecode, rate, out var tc, out string? parseError))
                    throw new InvalidOperationException(parseError ?? "Unreadable timecode.");

                lock (_gate) Discipline(tc.TotalFrames, rate);

                LastResponse = payload;
                LastError = null;
                State = TimecodeLinkState.Online;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = ex is HttpRequestException ? $"Cannot reach {Url}" : ex.Message;
                State = TimecodeLinkState.Offline;
                lock (_gate) { _haveBaseline = false; _lastIssued = -1; }
            }
        }
        while (await timer.WaitForNextTickSafeAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Folds a fresh server sample into the local clock. Caller holds <see cref="_gate"/>.
    /// </summary>
    private void Discipline(long sampleFrames, int rate)
    {
        long now = Stopwatch.GetTimestamp();
        long perDay = 24L * 3600L * rate;

        if (_haveBaseline && _rate == rate)
        {
            long freewheel = FreeWheelFrames(now, perDay);

            // Shortest way round the day, so a sample either side of midnight is not read
            // as being twenty-four hours out.
            long error = ((sampleFrames - freewheel) % perDay + perDay + perDay / 2) % perDay - perDay / 2;

            if (Math.Abs(error) <= (long)(SlewToleranceSeconds * rate))
            {
                _baseFrames = ((freewheel + Math.Sign(error)) % perDay + perDay) % perDay;
                _baseTimestamp = now;
                return;
            }
        }

        // A rate change, the first sample, or a master that has genuinely moved.
        _baseFrames = sampleFrames;
        _baseTimestamp = now;
        _rate = rate;
        _haveBaseline = true;
        _lastIssued = -1;
    }

    /// <summary>Where the free-wheeled clock stands. Caller holds <see cref="_gate"/>.</summary>
    private long FreeWheelFrames(long timestamp, long perDay)
    {
        double elapsed = (timestamp - _baseTimestamp) / (double)Stopwatch.Frequency;
        return ((_baseFrames + (long)(elapsed * _rate)) % perDay + perDay) % perDay;
    }

    /// <summary>Current timecode, free-wheeled from the last server sample.</summary>
    public bool TryGetCurrent(out Timecode timecode)
    {
        lock (_gate)
        {
            if (!_haveBaseline || _rate <= 0)
            {
                timecode = default;
                return false;
            }

            long perDay = 24L * 3600L * _rate;
            long frames = FreeWheelFrames(Stopwatch.GetTimestamp(), perDay);

            // A slew that pulled the baseline back would otherwise show the same second
            // counting down a frame. Hold instead, until the clock has caught up with what
            // was last shown. A day rolling over is a long way back, and is let through.
            if (_lastIssued >= 0 && frames < _lastIssued && _lastIssued - frames < _rate)
                frames = _lastIssued;

            _lastIssued = frames;
            timecode = new Timecode(frames, _rate);
            return true;
        }
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}

internal static class HttpClientJsonExtensions
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static async Task<TimecodeApiResponse?> GetFromJsonSafeAsync(this HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<TimecodeApiResponse>(stream, Options, ct).ConfigureAwait(false);
    }

    /// <summary>PeriodicTimer.WaitForNextTickAsync throws on cancellation; this returns false instead.</summary>
    public static async ValueTask<bool> WaitForNextTickSafeAsync(this PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}
