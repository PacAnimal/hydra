using Hydra.Config;
using Hydra.Keyboard;

namespace Hydra.Screen;

// The monitor switch hotkey's keys, on the input actor: the first direction pressed picks the edge, a perpendicular
// one pressed while it is held, with the same modifiers, picks that end's third of it, and releasing either makes the
// switch. A key is followed by its physical key, so letting go of a modifier first still switches. A direction along
// the same edge, or any other key pressed, abandons it; a third direction is ignored.
internal sealed class MonitorSwitchGesture
{
    private Held? _first;
    private Held? _second;

    // the switch a key event completes, if any
    public SwitchRequest? OnKey(KeyEvent ev, HotkeyAction? action)
    {
        if (ev.Type == KeyEventType.KeyUp)
        {
            if (_first is not { } first || (ev.PhysicalKey != first.Key && ev.PhysicalKey != _second?.Key)) return null;
            var request = new SwitchRequest(first.Direction, _second?.Direction);
            Reset();
            return request;
        }
        if (ev.IsRepeat) return null;

        if (action is not { } a || HotkeyBindings.SwitchDirection(a) is not { } direction)
        {
            Reset();
            return null;
        }
        var pressed = new Held(ev.PhysicalKey, direction, HotkeyBinding.ChordModifiers(ev));
        if (_first is not { } held) _first = pressed;
        else if (!direction.IsPerpendicularTo(held.Direction)) Reset();
        else if (_second == null && pressed.Modifiers == held.Modifiers) _second = pressed;
        return null;
    }

    private void Reset()
    {
        _first = null;
        _second = null;
    }

    private sealed record Held(int Key, Direction Direction, KeyModifiers Modifiers);
}

// where the monitor switch goes: across Edge, aimed at the middle of it or at the end third Third names
internal sealed record SwitchRequest(Direction Edge, Direction? Third);
