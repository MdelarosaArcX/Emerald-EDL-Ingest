namespace Emerald.Core;

/// <summary>
/// Which side of the low-memory thresholds free physical memory is on, and when that changes.
///
/// Said once per crossing, not once a sample: a level is entered below its threshold and left
/// only half a gigabyte above it, so memory hovering at the line does not fill the log.
/// </summary>
public sealed class LowMemoryWatch
{
    public enum Level { Normal, Low, Critical }

    /// <summary>Below this much free, Low: time to close something.</summary>
    public const long LowBytes = 3L * 1024 * 1024 * 1024;

    /// <summary>Below this much free, Critical: Windows is about to page.</summary>
    public const long CriticalBytes = 3L * 512 * 1024 * 1024;

    /// <summary>How far above a threshold memory must climb before the level is left.</summary>
    public const long HysteresisBytes = 512L * 1024 * 1024;

    public Level Current { get; private set; } = Level.Normal;

    /// <summary>The level just entered, or null when it has not changed.</summary>
    public Level? Check(long freeBytes)
    {
        Level next = Current switch
        {
            Level.Critical when freeBytes >= CriticalBytes + HysteresisBytes => freeBytes >= LowBytes + HysteresisBytes ? Level.Normal : Level.Low,
            Level.Critical => Level.Critical,
            Level.Low when freeBytes < CriticalBytes => Level.Critical,
            Level.Low when freeBytes >= LowBytes + HysteresisBytes => Level.Normal,
            Level.Low => Level.Low,
            _ when freeBytes < CriticalBytes => Level.Critical,
            _ when freeBytes < LowBytes => Level.Low,
            _ => Level.Normal,
        };

        if (next == Current) return null;

        Current = next;
        return next;
    }
}
