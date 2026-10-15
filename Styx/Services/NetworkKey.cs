namespace Styx.Services;

/// <summary>
/// Where a peer lives: a network and the sub-network path within it. Peers see and address only peers with the same
/// key. The path is a v2 token's <c>sub</c> caveats joined with '/', which no element can contain; empty is the
/// network itself, where every v1 token and every unnarrowed v2 token lives.
/// </summary>
public readonly record struct NetworkKey(Guid NetworkId, string Path)
{
    public static implicit operator NetworkKey(Guid networkId) => new(networkId, "");

    public override string ToString() => Path.Length == 0 ? NetworkId.ToString() : $"{NetworkId}/{Path}";
}
