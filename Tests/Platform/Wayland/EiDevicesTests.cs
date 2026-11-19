using Hydra.Platform;
using Hydra.Platform.Linux.Wayland;

namespace Tests.Platform.Wayland;

// which EIS devices a slave injects through and reports screens from. WaylandSession needs a portal and EiContext
// libei, so these test the pure helpers both decide through (EiDevices, SlaveScreenState.ScreensKnown).
[TestFixture]
public class EiDevicesTests
{
    private static readonly EiRegion Screen = new(0, 0, 1920, 1080);

    private static EiDevice Device(EiCapabilities capabilities, bool resumed, params EiRegion[] regions) =>
        new(0, capabilities.ToString(), capabilities, null, regions) { IsResumed = resumed };

    private static EiDevice Absolute(bool resumed) =>
        Device(EiCapabilities.PointerAbsolute | EiCapabilities.Scroll | EiCapabilities.Button, resumed, Screen);

    [Test]
    public void AddedButNotResumedAbsolutePointer_HasItsRegions()
    {
        EiDevice[] devices = [Absolute(false), Device(EiCapabilities.Pointer, false), Device(EiCapabilities.Keyboard, false)];
        Assert.That(EiDevices.AbsoluteRegions(devices), Is.EqualTo((EiRegion[])[Screen]));
    }

    [Test]
    public void ResumedAbsolutePointer_IsPreferredOverAStalePausedOne()
    {
        var fresh = new EiRegion(0, 0, 2560, 1440);
        EiDevice[] devices = [Absolute(false), Device(EiCapabilities.PointerAbsolute, true, fresh)];
        Assert.That(EiDevices.AbsoluteRegions(devices), Is.EqualTo((EiRegion[])[fresh]));
    }

    [Test]
    public void AddedButNotResumedDevices_AreNotReady()
    {
        EiDevice[] devices = [Absolute(false), Device(EiCapabilities.Pointer, false), Device(EiCapabilities.Keyboard, false)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(EiDevices.CanTypeAndPoint(devices), Is.False);
            Assert.That(EiDevices.Pick(devices, EiCapabilities.PointerAbsolute), Is.Null, "injection only through a resumed device");
        }
    }

    [Test]
    public void ResumedKeyboardAndPointer_AreReady()
    {
        EiDevice[] relative = [Device(EiCapabilities.Pointer, true), Device(EiCapabilities.Keyboard, true)];
        EiDevice[] absolute = [Absolute(true), Device(EiCapabilities.Keyboard, true)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(EiDevices.CanTypeAndPoint(relative), Is.True);
            Assert.That(EiDevices.CanTypeAndPoint(absolute), Is.True);
        }
    }

    [Test]
    public void ResumedPointerWithoutResumedKeyboard_IsNotReady()
    {
        EiDevice[] devices = [Absolute(true), Device(EiCapabilities.Keyboard, false)];
        Assert.That(EiDevices.CanTypeAndPoint(devices), Is.False);
    }

    [Test]
    public void ResumedKeyboardWithoutResumedPointer_IsNotReady()
    {
        EiDevice[] devices = [Absolute(false), Device(EiCapabilities.Pointer, false), Device(EiCapabilities.Keyboard, true)];
        Assert.That(EiDevices.CanTypeAndPoint(devices), Is.False);
    }

    [Test]
    public void RelativePointerOnly_HasNoRegions()
    {
        EiDevice[] devices = [Device(EiCapabilities.Pointer, true), Device(EiCapabilities.Keyboard, true)];
        Assert.That(EiDevices.AbsoluteRegions(devices), Is.Null);
    }

    [Test]
    public void ButtonsAndScroll_PreferTheResumedPointer()
    {
        var pointer = Device(EiCapabilities.Pointer | EiCapabilities.Button | EiCapabilities.Scroll, true);
        EiDevice[] devices = [Absolute(true), pointer];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(EiDevices.Pick(devices, EiCapabilities.Button), Is.SameAs(pointer));
            Assert.That(EiDevices.Pick(devices, EiCapabilities.Scroll), Is.SameAs(pointer));
        }
    }

    [Test]
    public void Regions_AreReportedBeforeReady() =>
        Assert.That(SlaveScreenState.ScreensKnown(false, [new DetectedScreen(0, 0, 1920, 1080, "region 1", null, null)]), Is.True);

    [Test]
    public void NoRegions_WaitForReady()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SlaveScreenState.ScreensKnown(false, null), Is.False, "a relative pointer only");
            Assert.That(SlaveScreenState.ScreensKnown(false, []), Is.False, "an absolute pointer without regions");
            Assert.That(SlaveScreenState.ScreensKnown(true, null), Is.True);
            Assert.That(SlaveScreenState.ScreensKnown(true, []), Is.True, "the wlroots path with no outputs read");
        }
    }
}
