using Hydra.Platform.Linux;

namespace Tests.Platform;

// the evdev master's recovery from SYN_DROPPED: the events it drops and the key changes it makes up afterwards
[TestFixture]
public class EvdevResyncTests
{
    private const ushort KeyA = EvdevNativeMethods.KEY_A;
    private const ushort Shift = EvdevNativeMethods.KEY_LEFTSHIFT;
    private const ushort Left = EvdevNativeMethods.BTN_LEFT;

    private static byte[] Down(params ushort[] codes)
    {
        var keys = new byte[EvdevResync.KeyBytes];
        foreach (var code in codes) keys[code / 8] |= (byte)(1 << (code % 8));
        return keys;
    }

    private static List<EvdevKeyChange> Changes(params EvdevKeyChange[] changes) => [.. changes];

    [Test]
    public void KeyBytes_CoverKeyMax() =>
        Assert.That(EvdevResync.KeyBytes * 8, Is.EqualTo(EvdevNativeMethods.KEY_MAX + 1));

    [Test]
    public void Diff_SameState_IsNothing() =>
        Assert.That(EvdevResync.Diff(new HashSet<ushort> { KeyA, Left }, Down(KeyA, Left)), Is.Empty);

    [Test]
    public void Diff_HeldButUp_IsReleased() =>
        Assert.That(EvdevResync.Diff(new HashSet<ushort> { KeyA, Left }, Down()),
            Is.EqualTo(Changes(new EvdevKeyChange(KeyA, false), new EvdevKeyChange(Left, false))));

    [Test]
    public void Diff_DownButNotHeld_IsPressed() =>
        Assert.That(EvdevResync.Diff(new HashSet<ushort>(), Down(Shift, Left)),
            Is.EqualTo(Changes(new EvdevKeyChange(Shift, true), new EvdevKeyChange(Left, true))));

    [Test]
    public void Diff_ReleasesComeBeforePresses() =>
        Assert.That(EvdevResync.Diff(new HashSet<ushort> { Left }, Down(KeyA)),
            Is.EqualTo(Changes(new EvdevKeyChange(Left, false), new EvdevKeyChange(KeyA, true))));

    [Test]
    public void Diff_KeyMax_IsSeen() =>
        Assert.That(EvdevResync.Diff(new HashSet<ushort>(), Down(EvdevNativeMethods.KEY_MAX)),
            Is.EqualTo(Changes(new EvdevKeyChange(EvdevNativeMethods.KEY_MAX, true))));

    [Test]
    public void Diff_ShortMask_ReadsMissingBitsAsUp() =>
        Assert.That(EvdevResync.Diff(new HashSet<ushort> { Left }, new byte[1]),
            Is.EqualTo(Changes(new EvdevKeyChange(Left, false))));

    [Test]
    public void IsStale_PressOfAHeldCode_IsStale() =>
        Assert.That(EvdevResync.IsStale(new HashSet<ushort> { KeyA }, KeyA, 1), Is.True);

    [Test]
    public void IsStale_ReleaseOfACodeNotHeld_IsStale() =>
        Assert.That(EvdevResync.IsStale(new HashSet<ushort>(), Left, 0), Is.True);

    [Test]
    public void IsStale_ChangesOfTheHeldState_AreNot()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(EvdevResync.IsStale(new HashSet<ushort>(), KeyA, 1), Is.False);
            Assert.That(EvdevResync.IsStale(new HashSet<ushort> { Left }, Left, 0), Is.False);
        }
    }

    [Test]
    public void IsStale_Repeat_IsNeverStale()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(EvdevResync.IsStale(new HashSet<ushort> { KeyA }, KeyA, 2), Is.False);
            Assert.That(EvdevResync.IsStale(new HashSet<ushort>(), KeyA, 2), Is.False);
        }
    }

    // the queued events behind a resync's SYN_REPORT, already counted by EVIOCGKEY, are dropped once each
    [Test]
    public void IsStale_EventsQueuedBehindAResync_AreDropped()
    {
        var held = new HashSet<ushort> { Left };
        foreach (var change in EvdevResync.Diff(held, Down(KeyA)))
            if (change.Down) held.Add(change.Code);
            else held.Remove(change.Code);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(EvdevResync.IsStale(held, KeyA, 1), Is.True, "the press EVIOCGKEY already showed");
            Assert.That(EvdevResync.IsStale(held, Left, 0), Is.True, "the release EVIOCGKEY already showed");
        }
    }

    [Test]
    public void Next_WithoutDrop_PassesEverything()
    {
        var sync = new EvdevResync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.Next(EvdevNativeMethods.EV_KEY, KeyA), Is.EqualTo(EvdevSyncAction.Pass));
            Assert.That(sync.Next(EvdevNativeMethods.EV_REL, EvdevNativeMethods.REL_X), Is.EqualTo(EvdevSyncAction.Pass));
            Assert.That(sync.Next(EvdevNativeMethods.EV_SYN, EvdevNativeMethods.SYN_REPORT), Is.EqualTo(EvdevSyncAction.Pass));
            Assert.That(sync.Dropping, Is.False);
        }
    }

    [Test]
    public void Next_AfterDrop_DiscardsUpToAndIncludingSynReport_ThenResyncs()
    {
        var sync = new EvdevResync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.Next(EvdevNativeMethods.EV_SYN, EvdevNativeMethods.SYN_DROPPED), Is.EqualTo(EvdevSyncAction.Discard));
            Assert.That(sync.Dropping, Is.True);
            Assert.That(sync.Next(EvdevNativeMethods.EV_KEY, KeyA), Is.EqualTo(EvdevSyncAction.Discard));
            Assert.That(sync.Next(EvdevNativeMethods.EV_REL, EvdevNativeMethods.REL_X), Is.EqualTo(EvdevSyncAction.Discard));
            Assert.That(sync.Next(EvdevNativeMethods.EV_SYN, EvdevNativeMethods.SYN_REPORT), Is.EqualTo(EvdevSyncAction.Resync));
            Assert.That(sync.Dropping, Is.False);
            Assert.That(sync.Next(EvdevNativeMethods.EV_KEY, KeyA), Is.EqualTo(EvdevSyncAction.Pass), "events after the report are read as usual");
        }
    }

    [Test]
    public void Next_SecondDropInTheGap_StillWaitsForOneSynReport()
    {
        var sync = new EvdevResync();
        sync.Next(EvdevNativeMethods.EV_SYN, EvdevNativeMethods.SYN_DROPPED);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.Next(EvdevNativeMethods.EV_SYN, EvdevNativeMethods.SYN_DROPPED), Is.EqualTo(EvdevSyncAction.Discard));
            Assert.That(sync.Next(EvdevNativeMethods.EV_SYN, EvdevNativeMethods.SYN_REPORT), Is.EqualTo(EvdevSyncAction.Resync));
            Assert.That(sync.Next(EvdevNativeMethods.EV_SYN, EvdevNativeMethods.SYN_REPORT), Is.EqualTo(EvdevSyncAction.Pass));
        }
    }
}
