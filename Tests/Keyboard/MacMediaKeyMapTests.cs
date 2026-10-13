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

    [Test]
    public void PhysicalKey_IsClearOfEveryKeycode()
    {
        // keycode 0 is 'a': a media key stamped 0 would pair with it in the router's held-key set and in KeyFates.
        // every type Decode can yield, against every CGKeyCode (a uint16)
        Assert.That(Enumerable.Range(0, 0x10000).Select(t => MacMediaKeyMap.PhysicalKey((uint)t)), Has.All.GreaterThan((int)ushort.MaxValue));
    }

    // data1: NX_KEYTYPE in bits 16-31, state 0xA (down) or 0xB (up) in bits 8-15, the repeat flag in bit 0
    [TestCase(0x00000A00L, 0u, true, false)]
    [TestCase(0x00000A01L, 0u, true, true)]
    [TestCase(0x00000B00L, 0u, false, false)]
    [TestCase(0x00000B01L, 0u, false, true)]
    [TestCase(0x00100A00L, 16u, true, false)]
    [TestCase(0x00010B00L, 1u, false, false)]
    [TestCase(unchecked((long)0xFFFFFFFF00030A01UL), 3u, true, true)]
    [TestCase(unchecked((long)0x80000000FFFF0B00UL), 0xFFFFu, false, false)]
    public void Decode_ReadsTypeStateAndRepeat(long data1, uint keyType, bool isDown, bool isRepeat) =>
        Assert.That(MacMediaKeyMap.Decode(data1), Is.EqualTo(new MacMediaKeyMap.NxKey(keyType, isDown, isRepeat)));
}
