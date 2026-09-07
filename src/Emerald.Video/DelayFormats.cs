using Emerald.Deltacast;

namespace Emerald.Video;

/// <summary>
/// Decides whether a receiver's format may be transmitted <b>raw</b>, and refuses in words
/// an operator can act on when it may not.
///
/// This exists because <see cref="VideoFormat.ForFrameRate"/> ends in a silent fallback to
/// 1080p25. That is right where it is used — the EDL scales everything through ffmpeg on the
/// way out, so any source plays on any board — and lethal here, where there is no scaler at
/// all. A 720p50 receiver would produce 1,843,200 bytes, the transmitter would open a
/// 1080-line raster expecting 4,147,200, and <see cref="SdiOutput.PushFrame"/> would copy the
/// short buffer into the top of the frame and put garbage on air.
///
/// So this is deliberately not a "best match". It is an exact match or a refusal.
/// </summary>
public static class DelayFormats
{
    /// <summary>
    /// The VHD_VIDEOSTANDARD values that are progressive 1080-line: 1080p25, 1080p30,
    /// 1080p24, 1080p60, 1080p50. Anything not on this list is refused, including the
    /// 1920x1080 that FromStandard invents for a standard it does not know.
    /// </summary>
    private static readonly HashSet<uint> ProgressiveHd = new() { 0, 1, 8, 9, 10 };

    /// <summary>
    /// The transmit format for a receiver's format, when the two can be the same picture.
    /// Returns false with <paramref name="problem"/> set to something an operator can read.
    /// </summary>
    public static bool TryMatch(CaptureFormat capture, out VideoFormat? format, out string? problem)
    {
        format = null;
        problem = null;

        // Interlaced first: 1080i50 and 1080p25 are both 1920x1080 at 25, so a raster and
        // rate check accepts an interlaced source and transmits its fields as frames.
        if (capture.IsInterlaced)
        {
            problem = $"Tidal lock cannot transmit {capture.Name}. The delay carries frames exactly as they " +
                      "arrive and the transmitter would send interlaced fields as progressive. " +
                      "Feed the receiver a progressive 1080-line format.";
            return false;
        }

        // An allowlist of standards, not a check on raster and rate. FromStandard invents
        // 1920x1080 at a fallback rate for anything it does not know, so a raster check would
        // accept that guess and transmit whatever the receiver actually had.
        if (!ProgressiveHd.Contains(capture.Standard))
        {
            problem = capture.Standard == CaptureFormat.UnknownStandard || capture.Width != 1920 || capture.Height != 1080
                ? $"Tidal lock cannot transmit {capture.Name}. The delay carries raw frames and nothing " +
                  "scales them, and the transmitter only offers progressive 1080-line standards. " +
                  "Feed the receiver 1080-line video, or delay a different source."
                : $"The receiver is locked to a standard Emerald does not recognise ({capture.Standard}). " +
                  "Tidal lock needs a known progressive 1080-line format, because it transmits what it " +
                  "receives without converting it.";
            return false;
        }

        VideoFormat candidate = VideoFormat.ForFrameRate(capture.FrameRate);

        // ForFrameRate falls back to 1080p25 for a rate it does not carry, so the result is
        // checked rather than trusted - the fallback is the very thing being guarded against.
        if (!candidate.IsExactMatchFor(capture.FrameRate) ||
            candidate.Width != capture.Width || candidate.Height != capture.Height)
        {
            problem = $"Tidal lock cannot transmit {capture.Name}: the transmitter has no matching " +
                      $"{capture.FrameRate} fps 1080-line standard.";
            return false;
        }

        format = candidate;
        return true;
    }
}
