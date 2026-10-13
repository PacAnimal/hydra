using Hydra.Keyboard;
using Hydra.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform.Windows;

// A WindowsInputHandler driven the way its hook thread drives it, on a clock and an OS key state the test controls,
// with everything it hands the router recorded.
internal sealed class HookedHandler
{
    public readonly HashSet<int> OsHeld = [];
    public readonly List<object> Handed = [];
    public readonly List<(double X, double Y)> Moves = [];
    public readonly List<(double X, double Y)> Deltas = [];
    public readonly WindowsInputHandler Handler;
    public int Now;

    // run as the router receives each kind, so a test can cross screens from inside the callback the way that crossing lands
    public Action? OnMove, OnDelta, OnKey, OnButton;

    public HookedHandler(int now = 0)
    {
        Now = now;
        Handler = new WindowsInputHandler(NullLogger<WindowsInputHandler>.Instance, TransitionTestHelper.Profile("home"))
        {
            IsKeyDown = vk => OsHeld.Contains(vk),
            TickCount = () => Now,
        };
        Handler.Subscribe(
            (x, y) =>
            {
                Moves.Add((x, y));
                OnMove?.Invoke();
            },
            (x, y) =>
            {
                Deltas.Add((x, y));
                OnDelta?.Invoke();
            },
            e =>
            {
                Handed.Add(e);
                OnKey?.Invoke();
            },
            e =>
            {
                Handed.Add(e);
                OnButton?.Invoke();
            },
            e => Handed.Add(e));
    }

    public List<KeyEvent> Keys => [.. Handed.OfType<KeyEvent>()];

    public bool Move(int x, int y, uint time, Action<int, int>? setCursorPos = null) =>
        Handler.HandleMouse(NativeMethods.WM_MOUSEMOVE, new MSLLHOOKSTRUCT { pt = new WINPOINT { x = x, y = y }, time = time },
            setCursorPos ?? NoWarp);

    public bool Down(int vk, uint flags = 0, uint time = 1) =>
        Handler.HandleKey(NativeMethods.WM_KEYDOWN, new KBDLLHOOKSTRUCT { vkCode = (uint)vk, flags = flags, time = time });

    public bool Up(int vk, uint flags = 0, uint time = 1) =>
        Handler.HandleKey(NativeMethods.WM_KEYUP, new KBDLLHOOKSTRUCT { vkCode = (uint)vk, flags = flags, time = time });

    public bool Mouse(int msg, uint mouseData = 0) => Handler.HandleMouse(msg, new MSLLHOOKSTRUCT { mouseData = mouseData }, NoWarp);

    public bool Click() => Mouse(NativeMethods.WM_LBUTTONDOWN);

    public bool Wheel() => Mouse(NativeMethods.WM_MOUSEWHEEL, 120u << 16);

    // a key pressed while local reached the OS, so it holds it down
    public void PressLocally(int vk, uint flags = 0)
    {
        Assert.That(Down(vk, flags), Is.False, "pre-condition: a local press passes through");
        OsHeld.Add(vk);
    }

    public static readonly Action<int, int> NoWarp = static (_, _) => { };
}
