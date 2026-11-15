using System.Runtime.InteropServices;
using System.Text.Json;
using Hydra.Platform.Linux;

namespace Tests.Desktops;

// typing from a master on one xkb layout into a slave on another, both real desktops: the master resolves each key on
// its own layout, the slave types the character it is sent on whatever its layout has
[Category("Desktops")]
[TestFixtureSource(nameof(Cases))]
public sealed class LayoutTests(DesktopKind master, string masterLayout, int masterGroup, DesktopKind slave, string slaveLayout, string? masterOptions)
    : DesktopPair(master, slave)
{
    private const int CXkbKeycode = Keys.KeyC + 8;

    // the master's group is the non-Latin one, but for the grp:switch case's, which holds the other. openbox is in every image's HYDRA_DESKTOP_KINDS, so Unsupported() keeps the
    // openbox→openbox pairs to the main docker image (the one with both gnome and kde) rather than also running them in
    // the kde-x11 and hyprland lanes; the hyprland vm lane runs the others
    private static readonly TestFixtureData[] Cases =
    [
        // two x11 servers on different layouts
        new(DesktopKind.Openbox, "us,ru", 1, DesktopKind.Openbox, "de", null),
        // two x11 servers, neither with a Latin layout
        new(DesktopKind.Openbox, "ru,ua", 1, DesktopKind.Openbox, "ru,gr", null),
        // a hyprland master and an x11 slave
        new(DesktopKind.Hyprland, "us,ru", 1, DesktopKind.Openbox, "de", null),
        // an x11 master with no Latin layout and a hyprland slave, which selects its own groups
        new(DesktopKind.Openbox, "ru,ua", 1, DesktopKind.Hyprland, "us,ru", null),
        // a hyprland master whose right Alt switches to ru while held
        new(DesktopKind.Hyprland, "us,ru", 0, DesktopKind.Openbox, "us", GroupSwitchOption),
    ];

    private const string GroupSwitchOption = "grp:switch";

    // per layout, physical keys typing a word on it, and a key to type a capital on; checked against xkeyboard-config's
    // symbols/ru (default "winkeys" over "common") and symbols/ua (default "unicode" over "legacy")
    private static readonly Dictionary<string, (int[] Word, int Capital)> Words = new()
    {
        // the same keys on us: ghbdtn, capital G
        ["us"] = ([Keys.KeyG, Keys.KeyH, Keys.KeyB, Keys.KeyD, Keys.KeyT, Keys.KeyN], Keys.KeyG),
        // привет: п AC05, р AC06, и AB05, в AC03, е AD05, т AB06; capital П
        ["ru"] = ([Keys.KeyG, Keys.KeyH, Keys.KeyB, Keys.KeyD, Keys.KeyT, Keys.KeyN], Keys.KeyG),
        // ї AD12, ж AC10, і AC02, є AC11, ґ BKSL (ua(unicode) over ua(legacy)): "їжієґ", all but ж on no other layout
        // here, ж the one a slave with ru types on its own key; capital І, which ru has nowhere
        ["ua"] = ([Keys.KeyRightBrace, Keys.KeySemicolon, Keys.KeyS, Keys.KeyApostrophe, Keys.KeyBackslash], Keys.KeyS),
    };

    // GTK's names for the modifier keys a chord presses before its key
    private static readonly HashSet<string> ModifierKeysyms =
        ["Control_L", "Control_R", "Shift_L", "Shift_R", "Alt_L", "Alt_R", "Super_L", "Super_R", "Meta_L", "Meta_R", "ISO_Level3_Shift"];

    protected override string? Unsupported() =>
        MasterKind == DesktopKind.Openbox && SlaveKind == DesktopKind.Openbox && !(Desktop.Available(DesktopKind.Gnome) && Desktop.Available(DesktopKind.Kde))
            ? "the openbox→openbox layout pairs run only in the main docker image's lane"
            : null;

    protected override string MasterLayout => masterLayout;
    protected override string SlaveLayout => slaveLayout;
    protected override string? MasterOptions => masterOptions;

    private string ActiveMasterLayout => masterLayout.Split(',')[masterGroup];

    // after the pair's own setup: a test may switch the group, so every test starts on the case's
    [SetUp]
    public Task LockMasterGroup() => MasterDesktop.Group(masterGroup);

    [Test]
    public async Task NativeLetters_ArriveAsTheSameCharacters()
    {
        var keys = Words[ActiveMasterLayout].Word;
        var expected = string.Concat(keys.Select(key => Typed(masterLayout, masterGroup, key, shifted: false)));
        await CrossRight();

        await Hands.TypeKeys(keys);

        var (typed, seen) = await NextTypedText(expected.Length);
        Assert.That(typed, Is.EqualTo(expected), $"the slave's app saw: {seen}");
    }

    [Test]
    public async Task ShiftedCapital_Arrives()
    {
        var key = Words[ActiveMasterLayout].Capital;
        var expected = Typed(masterLayout, masterGroup, key, shifted: true);
        await CrossRight();

        await Hands.Chord([Keys.KeyLeftShift], key);

        var (typed, seen) = await NextTypedText(expected.Length);
        Assert.That(typed, Is.EqualTo(expected), $"the slave's app saw: {seen}");
    }

    [Test]
    public async Task LatinGroup_TypesLatin()
    {
        using (var keymap = Compile(masterLayout))
        {
            if (!LatinShortcutKeysym.LatinLayouts(keymap.Handle)[0])
                Assert.Ignore($"'{masterLayout}' has no Latin group: its first layout is not Latin, so there is no Latin group to switch to");
        }
        await MasterDesktop.Group(0);
        await CrossRight();

        await Hands.Type("hello");

        var (typed, seen) = await NextTypedText("hello".Length);
        Assert.That(typed, Is.EqualTo("hello"), $"the slave's app saw: {seen}");
    }

    // the master sends Ctrl+'c' from its non-Latin group (LatinShortcutKeysym: its first Latin group's key, else the us
    // reference's). an x11 slave types the character exactly: on its C key where its active group (here its first, as
    // the slave's group is never locked) has c there, else on a spare key it binds to c, since its C key types с under
    // ru. a hyprland slave presses its Latin group's c key, selecting that group if another is effective. either way
    // the app sees the keysym c with Control held
    [Test]
    public async Task CtrlC_ArrivesAsCtrlPlusC()
    {
        bool onCKey;
        using (var keymap = Compile(slaveLayout))
        {
            onCKey = X11Slave
                ? LatinShortcutKeysym.BaseKeysym(keymap.Handle, CXkbKeycode, 0) == 'c'
                : LatinShortcutKeysym.LatinLayouts(keymap.Handle).Select((latin, group) => latin && LatinShortcutKeysym.BaseKeysym(keymap.Handle, CXkbKeycode, (uint)group) == 'c').Any(c => c);
        }
        await CrossRight();

        await Hands.Chord([Keys.KeyLeftCtrl], Keys.KeyC);

        var (key, seen) = await NextKeyPress();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(key.GetProperty("key").GetString(), Is.EqualTo("c"), $"the slave's app saw: {seen}");
            Assert.That(Mods(key), Does.Contain("control"), $"the slave's app saw: {seen}");
            if (onCKey)
                Assert.That(key.GetProperty("code").GetInt32(), Is.EqualTo(CXkbKeycode), $"c is on the slave's C key, but the slave's app saw: {seen}");
            else
                Assert.That(key.GetProperty("code").GetInt32(), Is.Not.EqualTo(CXkbKeycode), $"the slave's C key types no c, but the slave's app saw: {seen}");
        }
    }

    // the key left of the right Shift types '.' on ru and ua, so the master sends Ctrl+'/' as GTK and Qt read it
    // (LatinShortcutKeysym), with Control only. the slave must not add a Shift the user never held where its '/' is
    // shifted (de's Shift+7): an x11 slave presses a key typing '/' unshifted or binds one at every level, a hyprland
    // slave its Latin group's slash key
    [Test]
    public async Task PunctuationShortcut_HasNoExtraShift()
    {
        await CrossRight();

        await Hands.Chord([Keys.KeyLeftCtrl], Keys.KeySlash);

        var (key, seen) = await NextKeyPress();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(key.GetProperty("key").GetString(), Is.EqualTo("slash"), $"the slave's app saw: {seen}");
            Assert.That(Mods(key), Does.Contain("control"), $"the slave's app saw: {seen}");
            Assert.That(Mods(key), Does.Not.Contain("shift"), $"the slave's app saw: {seen}");
        }
    }

    // grp:switch makes right Alt ISO_Group_Shift: ru only while it is held. the master's EvdevKeyResolver takes the
    // locked group as Hyprland's reported group less the held keys' shift, which holds only if that report counts the
    // right Alt captured through EIS; so the slave gets ru's п under it and us's g after it
    [Test]
    public async Task HeldGroupSwitch_TypesTheOtherLayoutOnlyWhileHeld()
    {
        if (masterOptions != GroupSwitchOption)
            Assert.Ignore($"the master has no {GroupSwitchOption}");
        await CrossRight();

        await Hands.Key(EvdevNativeMethods.KEY_RIGHTALT, true);
        await Hands.Tap(Keys.KeyG);
        await Hands.Key(EvdevNativeMethods.KEY_RIGHTALT, false);
        await Hands.Tap(Keys.KeyG);

        var (typed, seen) = await NextTypedText(2);
        Assert.That(typed, Is.EqualTo("пg"), $"the slave's app saw: {seen}");
    }

    // the characters of the next count keys the slave's app read as characters, and every key event it saw meanwhile
    private async Task<(string Typed, string Seen)> NextTypedText(int count)
    {
        var seen = new List<JsonElement>();
        var typed = "";
        try
        {
            while (typed.Length < count)
            {
                typed += Char(await SlaveProbe.WaitFor(e =>
                {
                    if (!Probe.Has(e, "key"))
                        return false;
                    seen.Add(e);
                    return e.GetProperty("pressed").GetBoolean() && Char(e).Length > 0;
                }));
            }
        }
        catch (TimeoutException e)
        {
            Assert.Fail($"the slave's app read '{typed}' of {count} characters; it saw: {Join(seen)}\n{e.Message}");
        }
        return (typed, Join(seen));
    }

    // the next press of a key that is not a modifier, and every key event the slave's app saw up to it
    private async Task<(JsonElement Key, string Seen)> NextKeyPress()
    {
        var seen = new List<JsonElement>();
        try
        {
            var key = await SlaveProbe.WaitFor(e =>
            {
                if (!Probe.Has(e, "key"))
                    return false;
                seen.Add(e);
                return e.GetProperty("pressed").GetBoolean() && !ModifierKeysyms.Contains(e.GetProperty("key").GetString() ?? "");
            });
            return (key, Join(seen));
        }
        catch (TimeoutException e)
        {
            Assert.Fail($"no key press reached the slave's app; it saw: {Join(seen)}\n{e.Message}");
            throw;
        }
    }

    private static string Join(List<JsonElement> events) => events.Count == 0 ? "nothing" : string.Join(", ", events);

    private static string Char(JsonElement key) => key.GetProperty("char").GetString() ?? "";

    private static List<string> Mods(JsonElement key) =>
        [.. key.GetProperty("mods").EnumerateArray().Select(m => m.GetString() ?? "")];

    // what an evdev key types on a layout list's group, at level 0 or Shift's level 1, as the master's own layout has it
    private static string Typed(string layout, int group, int evdevKey, bool shifted)
    {
        using var keymap = Compile(layout);
        var count = EvdevNativeMethods.xkb_keymap_key_get_syms_by_level(keymap.Handle, (uint)evdevKey + 8, (uint)group, shifted ? 1u : 0u, out var syms);
        if (count != 1 || syms == 0)
            throw new InvalidOperationException($"evdev key {evdevKey} types {count} keysyms on '{layout}' group {group}");
        var codepoint = EvdevNativeMethods.xkb_keysym_to_utf32((uint)Marshal.ReadInt32(syms));
        if (codepoint == 0)
            throw new InvalidOperationException($"evdev key {evdevKey} types no character on '{layout}' group {group}");
        return char.ConvertFromUtf32((int)codepoint);
    }

    private static XkbKeymap Compile(string layout) => XkbKeymap.FromNames(new LinuxInputConfig.XkbNames(layout, "pc105", null, null));
}
