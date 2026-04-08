namespace Hydra.Keyboard;

// shared base for all platform special-key maps.
// subclasses expose only their platform-specific dictionary; TryGet and Reverse live here.
internal abstract class SpecialKeyMap
{
    protected abstract Dictionary<ulong, SpecialKey> Map { get; }

    private Dictionary<SpecialKey, ulong>? _reverse;

    internal IEnumerable<KeyValuePair<ulong, SpecialKey>> Entries => Map;

    internal bool TryGet(ulong code, out SpecialKey key) => Map.TryGetValue(code, out key);

    // first keysym per SpecialKey wins; aliases (e.g. ISO_Left_Tab) are forward-only
    internal Dictionary<SpecialKey, ulong> Reverse => _reverse ??= Map
        .GroupBy(kvp => kvp.Value)
        .ToDictionary(g => g.Key, g => g.First().Key);
}
