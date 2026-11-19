using Hydra.Platform.Linux;

namespace Tests.Keyboard;

// the compositor's layout group set on a real EvdevKeyResolver: the report is the effective group, a held group key
// counted in it once, and a pinned resolver (the Wayland master's at home) leaves the group to the compositor. They need
// libxkbcommon, so they self-skip off Linux and run in the test-linux CI job and run-tests.sh linux.
[TestFixture]
[Category("Linux")]
public class EvdevKeyResolverLayoutTests
{
    private const uint KeyG = 34, KeyCapsLock = 58, KeyRightAlt = 100;

    [OneTimeSetUp]
    public void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Requires Linux with libxkbcommon (run ./run-tests.sh linux)");
    }

    // grp:switch: right Alt chooses the second group only while held (xkeyboard-config symbols/group "switch")
    private static EvdevKeyResolver Resolver(string options) =>
        new(new LinuxInputConfig.XkbNames("us,ru", "pc105", null, options));

    private static char? TypeG(EvdevKeyResolver resolver)
    {
        var down = resolver.Resolve(KeyG, 1)!;
        _ = resolver.Resolve(KeyG, 0);
        return down[^1]!.Character;
    }

    private static void Tap(EvdevKeyResolver resolver, uint key)
    {
        _ = resolver.Resolve(key, 1);
        _ = resolver.Resolve(key, 0);
    }

    // grp:switch: the compositor's report counts the held switch key, as we do; the resolver keeps the report's group
    // rather than adding the key to it, and the release takes the key's group back off
    [Test]
    public void Unpinned_AHeldSwitchKey_ReportedSwitched_IsNotCountedTwice()
    {
        using var resolver = Resolver("grp:switch");

        _ = resolver.Resolve(KeyRightAlt, 1);
        resolver.SetLayout(1);
        var held = TypeG(resolver);
        _ = resolver.Resolve(KeyRightAlt, 0);
        var released = TypeG(resolver);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held, Is.EqualTo('п'), "the report's group, not the key's added to it");
            Assert.That(released, Is.EqualTo('g'), "the base group once the key is let go, until the next report");
        }
    }

    [Test]
    public void Unpinned_AReportWhileTheSwitchIsHeld_ThenItsRelease_GivesTheBaseGroup_UntilTheNextReport()
    {
        using var resolver = Resolver("grp:switch");

        resolver.SetLayout(0);
        _ = resolver.Resolve(KeyRightAlt, 1);
        var held = TypeG(resolver);
        resolver.SetLayout(1);
        _ = resolver.Resolve(KeyRightAlt, 0);
        var released = TypeG(resolver);
        resolver.SetLayout(0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held, Is.EqualTo('п'), "our own switch key moves an unpinned resolver");
            Assert.That(released, Is.EqualTo('g'));
            Assert.That(TypeG(resolver), Is.EqualTo('g'));
        }
    }

    [Test]
    public void Pinned_AfterAToggleKey_KeepsTheReport()
    {
        using var resolver = Resolver("grp:caps_toggle");

        resolver.SetLayout(1, pin: true);
        Tap(resolver, KeyCapsLock);

        Assert.That(TypeG(resolver), Is.EqualTo('п'), "the toggle is the compositor's to report");
    }

    [Test]
    public void Pinned_IgnoresAGroupToggle_UntilTheNextReport()
    {
        using var resolver = Resolver("grp:caps_toggle");

        resolver.SetLayout(0, pin: true);
        Tap(resolver, KeyCapsLock);
        var afterToggle = TypeG(resolver);
        // the compositor saw the toggle too and reports its group
        resolver.SetLayout(1, pin: true);
        var afterReport = TypeG(resolver);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterToggle, Is.EqualTo('g'), "the toggle is the compositor's to apply");
            Assert.That(afterReport, Is.EqualTo('п'));
        }
    }

    [Test]
    public void Unpinned_AGroupToggleStillMoves()
    {
        using var resolver = Resolver("grp:caps_toggle");

        resolver.SetLayout(0);
        Tap(resolver, KeyCapsLock);

        Assert.That(TypeG(resolver), Is.EqualTo('п'));
    }

    // pinned, the compositor's group stands until it reports again, a held switch key's too: it counts the key it saw
    // as well, so its report while the key is held is the effective group, not one the key adds to
    [Test]
    public void Pinned_AHeldSwitchKey_IsTheCompositorsToReport()
    {
        using var resolver = Resolver("grp:switch");

        resolver.SetLayout(0, pin: true);
        _ = resolver.Resolve(KeyRightAlt, 1);
        var held = TypeG(resolver);
        resolver.SetLayout(1, pin: true);
        var reported = TypeG(resolver);
        _ = resolver.Resolve(KeyRightAlt, 0);
        var released = TypeG(resolver);
        resolver.SetLayout(0, pin: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held, Is.EqualTo('g'), "until the compositor reports");
            Assert.That(reported, Is.EqualTo('п'), "not the key's group added to the report's");
            Assert.That(released, Is.EqualTo('п'), "until the compositor reports the release");
            Assert.That(TypeG(resolver), Is.EqualTo('g'));
        }
    }
}
