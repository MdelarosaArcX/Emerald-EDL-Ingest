namespace Emerald.Core;

/// <summary>
/// The station clock, once, for the whole application.
///
/// Emerald has one timecode generator, so it should have one connection to it and one
/// place to say where it lives. Before this, the capture deck, the EDL Generator and the
/// Ingest Controller each built their own <see cref="TimecodeService"/> and each polled the
/// same endpoint independently — three sockets, three free-wheeling clocks that could sit a
/// frame apart, and an address that could only be changed in one of the three windows.
///
/// Now they share this. Changing the generator's address anywhere changes it everywhere,
/// immediately: the link re-dials, the new address is written to settings.json, and
/// <see cref="UrlChanged"/> tells every open window to show it. An operator moving the
/// generator to a new machine touches one field, in whichever module happens to be in
/// front of them.
///
/// One process is what makes this possible, and it is the same reason the modules can share
/// a receiver — see the single-instance handling in Emerald.App.
/// </summary>
public static class TimecodeLink
{
    private static readonly object Gate = new();

    private static AppSettings? _settings;
    private static bool _connected;

    /// <summary>The one disciplined clock. Never disposed by a window; only by application exit.</summary>
    public static TimecodeService Service { get; } = new();

    /// <summary>Where the generator is, as last set.</summary>
    public static string Url => _settings?.TimecodeApiUrl ?? "";

    /// <summary>
    /// Raised when the address changes, so every open module can show the new one. Handlers
    /// run on the caller's thread; a window must unsubscribe when it closes, because this is
    /// static and would otherwise outlive it.
    /// </summary>
    public static event Action<string>? UrlChanged;

    /// <summary>
    /// Joins the shared link, starting it on the first call. Every window calls this; only
    /// the first one actually dials.
    /// </summary>
    public static void Connect(AppSettings settings)
    {
        lock (Gate)
        {
            _settings = settings;

            if (_connected) return;
            _connected = true;
        }

        Service.Start(settings.TimecodeApiUrl);
    }

    /// <summary>
    /// Points the link at a different generator and remembers it. Returns false, with a
    /// reason, when the address is not one — nothing is changed in that case, because a
    /// half-applied clock address is worse than the old one.
    /// </summary>
    public static bool TrySetUrl(string? url, out string? problem)
    {
        string trimmed = (url ?? "").Trim();

        if (trimmed.Length == 0)
        {
            problem = "The timecode generator address is empty.";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            problem = $"Not a valid address: {trimmed}";
            return false;
        }

        AppSettings? settings;
        lock (Gate) settings = _settings;

        if (settings is null)
        {
            problem = "The timecode link has not been connected yet.";
            return false;
        }

        problem = null;

        if (settings.TimecodeApiUrl == trimmed)
        {
            // Same address: re-dial anyway, since Apply on an unchanged field is how an
            // operator asks a link that has gone offline to try again.
            Redial();
            return true;
        }

        settings.TimecodeApiUrl = trimmed;
        settings.Save();

        Service.Start(trimmed);
        UrlChanged?.Invoke(trimmed);
        return true;
    }

    /// <summary>Re-dials the current address, for a link that has dropped.</summary>
    public static void Redial()
    {
        AppSettings? settings;
        lock (Gate) settings = _settings;

        if (settings is not null) Service.Start(settings.TimecodeApiUrl);
    }

    /// <summary>Application exit. Windows never call this — they do not own the clock.</summary>
    public static void Shutdown()
    {
        lock (Gate) _connected = false;
        Service.Dispose();
    }
}
