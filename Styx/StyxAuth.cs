using System.Runtime.CompilerServices;
using Cathedral.Styx;

namespace Styx;

/// <summary>
/// What a relay authenticates with. <see cref="Keys"/> sign and verify v2 tokens, the first one minting;
/// <see cref="V1Password"/>, when set, decrypts v1 tokens and guards <c>/api/network-config</c>. Revocations apply
/// to both token versions.
/// </summary>
public sealed record StyxAuth(
    IReadOnlyList<byte[]> Keys,
    string? V1Password,
    IReadOnlySet<Guid> RevokedNetworks,
    IReadOnlySet<string> RevokedIds)
{
    /// <summary>Hydra's embedded relay: v1 only, under the password every machine derives its token from.</summary>
    public static StyxAuth V1Only(string password) => new([], password, new HashSet<Guid>(), new HashSet<string>());

    public bool AcceptsV1 => !string.IsNullOrEmpty(V1Password);

    public bool CanMint => Keys.Count > 0;

    // network creation mints a v2 root, and checks the relay password before it does
    public bool CreatesNetworks => CanMint && AcceptsV1;

    // computed once per key list, so a login does not hash every key; kept outside the record so equality and
    // `with` copies are unaffected
    private static readonly ConditionalWeakTable<IReadOnlyList<byte[]>, Dictionary<byte, byte[]>> KeyIds = [];

    public byte[]? KeyFor(byte keyId) =>
        KeyIds.GetValue(Keys, keys => keys.ToDictionary(key => StyxToken.KeyIdOf(key))).GetValueOrDefault(keyId);

    // secrets stay out of logs and debugger displays
    public override string ToString() =>
        $"StyxAuth {{ Keys = {Keys.Count}, AcceptsV1 = {AcceptsV1} }}";
}
