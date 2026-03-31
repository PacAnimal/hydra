using Hydra.Platform.MacOs;

namespace Tests.Keyboard;

[TestFixture]
public class AltGrDetectionTests
{
    // -- option held + printable character + no command = AltGr --

    [TestCase('a', false, true, true)]
    [TestCase('@', false, true, true)]
    [TestCase('€', false, true, true)]
    [TestCase('#', false, true, true)]
    public void PrintableChar_OptionHeld_NoCommand_IsAltGr(char c, bool isCommand, bool optionHeld, bool expected) =>
        Assert.That(MacKeyResolver.DetectAltGr(c, isCommand, optionHeld), Is.EqualTo(expected));

    // -- printable character WITHOUT option = not AltGr (just a regular keypress) --

    [TestCase('a', false, false)]
    [TestCase('@', false, false)]
    public void PrintableChar_OptionNotHeld_IsNotAltGr(char c, bool isCommand, bool optionHeld) =>
        Assert.That(MacKeyResolver.DetectAltGr(c, isCommand, optionHeld), Is.False);

    // -- option held + command = not AltGr (it's a keyboard shortcut) --

    [TestCase('a', true, true)]
    [TestCase('c', true, true)]
    public void PrintableChar_WithCommand_IsNotAltGr(char c, bool isCommand, bool optionHeld) =>
        Assert.That(MacKeyResolver.DetectAltGr(c, isCommand, optionHeld), Is.False);

    // -- no character (special key output) = not AltGr --

    [Test]
    public void NoCharacter_IsNotAltGr() =>
        Assert.That(MacKeyResolver.DetectAltGr(null, false, true), Is.False);
}
