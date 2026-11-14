namespace Tests.Desktops;

[Category("Desktops")]
[TestFixtureSource(typeof(DesktopPairs), nameof(DesktopPairs.All))]
public sealed class HotkeyTests(DesktopKind master, DesktopKind slave) : DesktopPair(master, slave)
{
    // InputRouter's lines for the cursor lock hotkey: a master with a screen of its own locks to it, a remote-only one
    // with none confines the cursor to the remote screen it is on
    private const string ScreenLocked = "Screen lock: locked";
    private const string ScreenUnlocked = "Screen lock: unlocked";
    private const string RemoteOnlyConfined = "Cursor lock: confined to current screen";
    private const string RemoteOnlyFree = "Cursor lock: free to roam";
    // a wayland master's lines once it bound its hotkeys as global shortcuts, and as the portal fires the lock's; a
    // hyprland master's once it reads them from the keyboards instead (the test vm runs hydra as root)
    private const string HotkeysBound = "Hotkeys at home:";
    private const string HotkeysFromKeyboards = "Hotkeys at home: read from the keyboards (evdev)";
    private const string LockShortcutActivated = "Global shortcut ToggleCursorLock-0 activated";

    private string LockedLine => EvdevMaster ? RemoteOnlyConfined : ScreenLocked;
    private string UnlockedLine => EvdevMaster ? RemoteOnlyFree : ScreenUnlocked;
    private bool MasterIsHyprland => MasterKind == DesktopKind.Hyprland;

    // at home, where a wayland master sees no key: the chord reaches it as the desktop's global shortcut (the test
    // portal's on gnome and kde), or read from the keyboards on hyprland; x11 and evdev masters read the keys themselves
    [Test]
    public async Task CursorLockHotkey_AtHome_TogglesTheLock()
    {
        if (!EvdevMaster)
            Assert.That(Visiting(), Is.False, "the cursor is not home");
        if (MasterIsWayland)
        {
            await Master.WaitForLog(MasterIsHyprland ? HotkeysFromKeyboards : HotkeysBound);
            // the portal fires no shortcut while a capture holds the keys, and the master ignores one then
            await WaitForCaptureReleased();
        }
        // a lock an earlier test left on is let go first, so this one starts unlocked
        if (Master.LastIndexOf(LockedLine) > Master.LastIndexOf(UnlockedLine))
            await Toggle(UnlockedLine);

        var from = Master.Log.Count;
        var locked = await Toggle(LockedLine);
        try
        {
            if (MasterIsWayland && !MasterIsHyprland)
            {
                var activated = Master.IndexOf(LockShortcutActivated, from);
                Assert.That(activated, Is.GreaterThanOrEqualTo(0).And.LessThan(locked),
                    "the lock did not come from the desktop's global shortcut");
            }
            if (MasterIsHyprland)
                Assert.That(Master.IndexOf(LockShortcutActivated, from), Is.EqualTo(-1), "the lock came from a global shortcut, not the keyboards");
            Assert.That(Master.IndexOf(UnlockedLine, from), Is.EqualTo(-1), "one press toggled the lock twice");
        }
        finally
        {
            await Toggle(UnlockedLine);
        }
    }

    // taps the cursor lock hotkey on the master; the index of the lock line it logs
    private async Task<int> Toggle(string expected)
    {
        var from = Master.Log.Count;
        await Hands.Tap(Keys.CursorLockHotkey);
        return await Master.WaitForLog(expected, from);
    }
}
