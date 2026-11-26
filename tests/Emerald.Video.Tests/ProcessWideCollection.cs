using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// Tests that measure the whole test process - its handles, its memory - and so must not run while
/// other tests start ffmpeg or open files alongside them. xunit runs this collection on its own.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWideCollection
{
    public const string Name = "Process-wide measurements";
}
