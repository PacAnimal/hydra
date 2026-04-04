namespace Hydra.Keyboard;

// shared base for all platform special-key maps.
// subclasses expose only their platform-specific dictionary; TryGet and Reverse live here.
internal abstract class SpecialKeyMap
{
    protected abstract Dictionary<ulong, SpecialKey> Map { get; }

    private Dictionary<SpecialKey, ulong>? _reverse;

    internal bool TryGet(ulong code, out SpecialKey key) => Map.TryGetValue(code, out key);

    internal Dictionary<SpecialKey, ulong> Reverse => _reverse ??= Map.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);
}
