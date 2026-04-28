using Hydra.Keyboard;
using Hydra.Platform.MacOs;

namespace Tests.Keyboard;

[TestFixture]
public class MacModifierMappingTests
{
    // CGEventFlag mask values (matching NativeMethods constants)
    private const ulong AlphaShift = 0x00010000;
    private const ulong Shift = 0x00020000;
    private const ulong Control = 0x00040000;
    private const ulong Alternate = 0x00080000;
    private const ulong Command = 0x00100000;
    private const ulong NumericPad = 0x00200000;

    // -- output direction: KeyModifiers → CGEventFlags --

    [Test]
    public void MapToFlags_SuperOnly_ReturnsCommandFlag() =>
        Assert.That(MacOutputHandler.MapModifiersToFlags(KeyModifiers.Super), Is.EqualTo(Command));

    [Test]
    public void MapToFlags_NumLock_NotMappedToNumericPad()
    {
        // Linux NumLock is a system-wide lock state; macOS NumericPad is a per-key identity flag.
        // Injecting NumericPad on regular keys (e.g. 'a') breaks Cmd+A in Chromium-based apps.
        var flags = MacOutputHandler.MapModifiersToFlags(KeyModifiers.NumLock);
        Assert.That((flags & NumericPad), Is.Zero);
    }

    [Test]
    public void MapToFlags_SuperWithNumLock_NoNumericPad()
    {
        // Win+A on Linux master → Cmd+A on Mac slave must NOT have NumericPad set
        var flags = MacOutputHandler.MapModifiersToFlags(KeyModifiers.Super | KeyModifiers.NumLock);
        Assert.That(flags, Is.EqualTo(Command));
    }

    [Test]
    public void MapToFlags_AllModifiers_RoundTrip()
    {
        var mods = KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super | KeyModifiers.CapsLock;
        var flags = MacOutputHandler.MapModifiersToFlags(mods);
        Assert.That(flags, Is.EqualTo(Shift | Control | Alternate | Command | AlphaShift));
    }

    [Test]
    public void NoFlags_ReturnsNone() =>
        Assert.That(MacKeyResolver.MapModifiers(0), Is.EqualTo(KeyModifiers.None));

    [Test]
    public void ShiftFlag_ReturnsShift() =>
        Assert.That(MacKeyResolver.MapModifiers(Shift), Is.EqualTo(KeyModifiers.Shift));

    [Test]
    public void ControlFlag_ReturnsControl() =>
        Assert.That(MacKeyResolver.MapModifiers(Control), Is.EqualTo(KeyModifiers.Control));

    [Test]
    public void AlternateFlag_ReturnsAlt() =>
        Assert.That(MacKeyResolver.MapModifiers(Alternate), Is.EqualTo(KeyModifiers.Alt));

    [Test]
    public void CommandFlag_ReturnsSuperNotAlt()
    {
        // macOS Command maps to Super (cross-platform), not Meta
        var mods = MacKeyResolver.MapModifiers(Command);
        Assert.That(mods, Is.EqualTo(KeyModifiers.Super));
        Assert.That(mods.HasFlag(KeyModifiers.Alt), Is.False);
    }

    [Test]
    public void AlphaShiftFlag_ReturnsCapsLock() =>
        Assert.That(MacKeyResolver.MapModifiers(AlphaShift), Is.EqualTo(KeyModifiers.CapsLock));

    [Test]
    public void NumericPadFlag_DoesNotMapToNumLock() =>
        // NumericPad is a source flag ("key came from numpad"), not a NumLock state indicator
        Assert.That(MacKeyResolver.MapModifiers(NumericPad), Is.EqualTo(KeyModifiers.None));

    [Test]
    public void MultipleFlags_ReturnsCombination()
    {
        var mods = MacKeyResolver.MapModifiers(Shift | Control);
        Assert.That(mods, Is.EqualTo(KeyModifiers.Shift | KeyModifiers.Control));
    }

    [Test]
    public void AllModifiers_MapsCorrectly()
    {
        var mods = MacKeyResolver.MapModifiers(AlphaShift | Shift | Control | Alternate | Command);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mods.HasFlag(KeyModifiers.Shift), Is.True);
            Assert.That(mods.HasFlag(KeyModifiers.Control), Is.True);
            Assert.That(mods.HasFlag(KeyModifiers.Alt), Is.True);
            Assert.That(mods.HasFlag(KeyModifiers.Super), Is.True);
            Assert.That(mods.HasFlag(KeyModifiers.CapsLock), Is.True);
            Assert.That(mods.HasFlag(KeyModifiers.NumLock), Is.False);
        }
    }
}
