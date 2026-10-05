using Hydra.Keyboard;
using Hydra.Platform.MacOs;

namespace Tests.Keyboard;

[TestFixture]
public class MacMediaKeyMapTests
{
    // NX_KEYTYPE_* values from IOKit's ev_keymap.h
    [TestCase(SpecialKey.AudioVolumeUp, 0u)]
    [TestCase(SpecialKey.AudioVolumeDown, 1u)]
    [TestCase(SpecialKey.BrightnessUp, 2u)]
    [TestCase(SpecialKey.BrightnessDown, 3u)]
    [TestCase(SpecialKey.AudioMute, 7u)]
    [TestCase(SpecialKey.Eject, 14u)]
    [TestCase(SpecialKey.AudioPlay, 16u)]
    [TestCase(SpecialKey.AudioNext, 17u)]
    [TestCase(SpecialKey.AudioPrev, 18u)]
    public void OutputKey_RoundTripsThroughItsNxKeyType(SpecialKey key, uint nxType)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MacMediaKeyMap.TryGetNxKeyType(key, out var injected), Is.True);
            Assert.That(injected, Is.EqualTo(nxType));
            Assert.That(MacMediaKeyMap.TryGetKey(nxType, out var captured), Is.True);
            Assert.That(captured, Is.EqualTo(key));
        }
    }

    [Test]
    public void EveryOutputKey_RoundTrips()
    {
        Assert.That(MacMediaKeyMap.OutputKeys, Has.Count.EqualTo(9));
        foreach (var (key, nxType) in MacMediaKeyMap.OutputKeys)
            Assert.That(MacMediaKeyMap.TryGetKey(nxType, out var captured) && captured == key, Is.True, $"{key}");
    }

    // fast-forward and rewind are captured as next and previous, but never injected
    [TestCase(19u, SpecialKey.AudioNext, 17u)]
    [TestCase(20u, SpecialKey.AudioPrev, 18u)]
    public void InputOnlyAlias_CapturesAsItsKey_ButInjectsTheCanonicalType(uint alias, SpecialKey key, uint injected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MacMediaKeyMap.TryGetKey(alias, out var captured), Is.True);
            Assert.That(captured, Is.EqualTo(key));
            Assert.That(MacMediaKeyMap.TryGetNxKeyType(key, out var nxType), Is.True);
            Assert.That(nxType, Is.EqualTo(injected));
        }
    }

    [Test]
    public void NonMediaKeys_AreNotMapped()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MacMediaKeyMap.TryGetNxKeyType(SpecialKey.F1, out _), Is.False);
            Assert.That(MacMediaKeyMap.TryGetKey(4u, out _), Is.False);
        }
    }
}
