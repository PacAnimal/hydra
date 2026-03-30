using Hydra.Keyboard;
using Hydra.Platform.MacOs;

namespace Tests.Keyboard;

[TestFixture]
public class AltGrDetectionTests
{
    // -- option held + printable glyph + no command = AltGr --

    [TestCase((uint)'a', false, true, true)]
    [TestCase((uint)'@', false, true, true)]
    [TestCase((uint)'€', false, true, true)]
    [TestCase((uint)'#', false, true, true)]
    public void PrintableGlyph_OptionHeld_NoCommand_IsAltGr(uint keyId, bool isCommand, bool optionHeld, bool expected) =>
        Assert.That(MacKeyResolver.DetectAltGr(keyId, isCommand, optionHeld), Is.EqualTo(expected));

    // -- printable glyph WITHOUT option = not AltGr (just a regular keypress) --

    [TestCase((uint)'a', false, false)]
    [TestCase((uint)'@', false, false)]
    public void PrintableGlyph_OptionNotHeld_IsNotAltGr(uint keyId, bool isCommand, bool optionHeld) =>
        Assert.That(MacKeyResolver.DetectAltGr(keyId, isCommand, optionHeld), Is.False);

    // -- option held + command = not AltGr (it's a keyboard shortcut) --

    [TestCase((uint)'a', true, true)]
    [TestCase((uint)'c', true, true)]
    public void PrintableGlyph_WithCommand_IsNotAltGr(uint keyId, bool isCommand, bool optionHeld) =>
        Assert.That(MacKeyResolver.DetectAltGr(keyId, isCommand, optionHeld), Is.False);

    // -- special/control keys = not AltGr regardless of option or command --

    [TestCase(KeyId.Left)]
    [TestCase(KeyId.F1)]
    [TestCase(KeyId.Return)]
    [TestCase(KeyId.BackSpace)]
    [TestCase(KeyId.Escape)]
    [TestCase(KeyId.Shift_L)]
    [TestCase(KeyId.Delete)]
    public void SpecialKey_IsNotAltGr(uint keyId) =>
        Assert.That(MacKeyResolver.DetectAltGr(keyId, false, true), Is.False);

    // -- keypad digits with option held ARE AltGr candidates --

    [TestCase(KeyId.KP_0)]
    [TestCase(KeyId.KP_9)]
    [TestCase(KeyId.KP_Equal)]
    public void KeypadDigit_OptionHeld_NoCommand_IsAltGr(uint keyId) =>
        Assert.That(MacKeyResolver.DetectAltGr(keyId, false, true), Is.True);

    [Test]
    public void None_IsNotAltGr() =>
        Assert.That(MacKeyResolver.DetectAltGr(KeyId.None, false, true), Is.False);
}
