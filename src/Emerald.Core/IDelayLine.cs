namespace Emerald.Core;

/// <summary>
/// A delay line, as the parts of Emerald that only need to <i>report</i> on one see it.
///
/// The delay line itself lives in Emerald.Video, next to the recorder that feeds it and the
/// transmitter that drains it. <see cref="TidalLock"/> lives here, in a project that
/// references nothing, and only ever needs to answer "how full is it" and "has it finished" —
/// so it holds this rather than the thing itself.
/// </summary>
public interface IDelayLine
{
    int Width { get; }
    int Height { get; }
    int FrameRate { get; }

    /// <summary>Frames the recorder has committed since it rolled.</summary>
    long FramesWritten { get; }

    /// <summary>The delay, in frames: how far behind the receiver the transmitter runs.</summary>
    long TargetFrames { get; }

    /// <summary>True once the recording has stopped and no more frames are coming.</summary>
    bool IsSealed { get; }

    /// <summary>Frames written at the moment it was sealed; the drain ends here.</summary>
    long SealedAt { get; }

    /// <summary>The ring file, for the log.</summary>
    string Path { get; }
}
