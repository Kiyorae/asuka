namespace Asuka.Core;

/// <summary>A local image collection entry owned by one simulated account.</summary>
public sealed record CustomFace(string SelfId, Asset Asset, DateTimeOffset AddedAt, long Sequence)
{
    public string Name => Asset.Name;
}
