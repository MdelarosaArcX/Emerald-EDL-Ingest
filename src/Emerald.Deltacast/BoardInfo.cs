namespace Emerald.Deltacast;

public sealed class BoardInfo
{
    public required uint Index { get; init; }
    public required string Model { get; init; }
    public required uint BoardType { get; init; }
    public required string BoardTypeName { get; init; }
    public required int RxCount { get; init; }
    public required int TxCount { get; init; }

    /// <summary>Rendered as "0. DELTA-3G-elp-d 22" to match the DELTACAST tooling convention.</summary>
    public string DisplayName => $"{Index}. {Model}";

    public IReadOnlyList<ChannelPort> RxPorts { get; init; } = Array.Empty<ChannelPort>();
    public IReadOnlyList<ChannelPort> TxPorts { get; init; } = Array.Empty<ChannelPort>();

    public override string ToString() => DisplayName;
}

public sealed record ChannelPort(string Kind, int Index)
{
    public string Name => $"{Kind}{Index}";
    public override string ToString() => Name;
}
