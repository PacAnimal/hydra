using Hydra.Keyboard;
using Hydra.Platform.MacOs;

namespace Tests.Keyboard;

// a key's repeats and up go where its press went: home to the OS, or away to a remote screen
[TestFixture]
public class KeyFatesTests
{
    private const int Key = 42;

    private static bool Down(KeyFates f, bool isVirtual, bool? repeat = null, bool osHolds = false) =>
        f.Swallow(Key, isUp: false, repeat, osHolds, isVirtual);

    private static bool Up(KeyFates f, bool isVirtual, bool osHolds = false) =>
        f.Swallow(Key, isUp: true, repeat: null, osHolds, isVirtual);

    [Test]
    public void PressedHome_ItsUpPassesEvenWhileVirtual()
    {
        var f = new KeyFates(strayUpsPass: false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Down(f, isVirtual: false), Is.False);
            Assert.That(Up(f, isVirtual: true), Is.False);
        }
    }

    [Test]
    public void PressedAway_ItsUpIsSwallowedEvenWhileLocal()
    {
        var f = new KeyFates(strayUpsPass: false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Down(f, isVirtual: true), Is.True);
            Assert.That(Up(f, isVirtual: false), Is.True);
        }
    }

    [TestCase(null)]
    [TestCase(true)]
    public void PressedHome_ItsRepeatsPassOnlyWhileLocal(bool? repeat)
    {
        var f = new KeyFates(strayUpsPass: false);
        Down(f, isVirtual: false, repeat: repeat is null ? null : false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Down(f, isVirtual: false, repeat), Is.False);
            Assert.That(Down(f, isVirtual: true, repeat), Is.True, "a key held across an edge stops repeating at home");
            Assert.That(Down(f, isVirtual: false, repeat), Is.False);
        }
    }

    [TestCase(null)]
    [TestCase(true)]
    public void PressedAway_ItsRepeatsAreSwallowedEvenWhileLocal(bool? repeat)
    {
        var f = new KeyFates(strayUpsPass: false);
        Down(f, isVirtual: true, repeat: repeat is null ? null : false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Down(f, isVirtual: false, repeat), Is.True);
            Assert.That(Down(f, isVirtual: true, repeat), Is.True);
        }
    }

    [Test]
    public void MacMediaKey_DoesNotShareARecordWithTheKeycodeOfItsType()
    {
        // one macOS instance holds keycodes and media keys: keycode 0 is 'a', and NX type 0 is volume up
        var f = new KeyFates(strayUpsPass: true);
        var media = MacMediaKeyMap.PhysicalKey(0);
        f.Swallow(0, isUp: false, repeat: false, osHolds: false, isVirtual: true);
        f.Swallow(media, isUp: false, repeat: false, osHolds: false, isVirtual: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(f.Swallow(0, isUp: true, repeat: null, osHolds: false, isVirtual: false), Is.True, "pressed away");
            Assert.That(f.Swallow(media, isUp: true, repeat: null, osHolds: false, isVirtual: false), Is.False, "pressed home");
        }
    }

    [Test]
    public void AnUpClearsTheRecord()
    {
        var f = new KeyFates(strayUpsPass: false);
        Down(f, isVirtual: true);
        Up(f, isVirtual: true);

        Assert.That(Down(f, isVirtual: false), Is.False, "a fresh press, not a repeat of the one that went away");
    }

    [Test]
    public void AnUpTheOsHolds_AlwaysPasses()
    {
        var f = new KeyFates(strayUpsPass: false);
        Down(f, isVirtual: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Up(f, isVirtual: true, osHolds: true), Is.False, "recorded away");
            Assert.That(Up(f, isVirtual: true, osHolds: true), Is.False, "unrecorded");
        }
    }

    [TestCase(false, false)]
    [TestCase(true, true)]
    public void UnrecordedUp_FollowsTheFlag(bool isVirtual, bool swallowed) =>
        Assert.That(Up(new KeyFates(strayUpsPass: false), isVirtual), Is.EqualTo(swallowed));

    [TestCase(false)]
    [TestCase(true)]
    public void UnrecordedUp_WhereStrayUpsPass_Passes(bool isVirtual) =>
        Assert.That(Up(new KeyFates(strayUpsPass: true), isVirtual), Is.False);

    [TestCase(false, false)]
    [TestCase(true, true)]
    public void UnrecordedRepeat_FollowsTheFlag(bool isVirtual, bool swallowed) =>
        Assert.That(Down(new KeyFates(strayUpsPass: true), isVirtual, repeat: true), Is.EqualTo(swallowed));

    [Test]
    public void UnrecordedKeyTheOsHolds_IsRecordedHome()
    {
        // held before we saw its press: its repeats stay home while virtual and resume when local, its up passes
        var f = new KeyFates(strayUpsPass: false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Down(f, isVirtual: true, osHolds: true), Is.True);
            Assert.That(Down(f, isVirtual: false, osHolds: true), Is.False);
            Assert.That(Up(f, isVirtual: true), Is.False);
        }
    }

    [Test]
    public void AFreshPressByTheRepeatBit_ReRecords()
    {
        // a stale record (an up we never saw) heals at the next press
        var f = new KeyFates(strayUpsPass: true);
        Down(f, isVirtual: true, repeat: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Down(f, isVirtual: false, repeat: false), Is.False);
            Assert.That(Down(f, isVirtual: false, repeat: true), Is.False);
            Assert.That(Up(f, isVirtual: true), Is.False);
        }
    }

    [Test]
    public void Clear_ForgetsEveryRecord()
    {
        var f = new KeyFates(strayUpsPass: false);
        Down(f, isVirtual: true);
        f.Clear();

        Assert.That(Up(f, isVirtual: false), Is.False);
    }
}
