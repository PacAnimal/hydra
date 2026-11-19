using Hydra.Platform.Linux.Wayland;
using Microsoft.Extensions.Logging;
using Tests.Setup;
using Tmds.DBus.Protocol;

namespace Tests.Platform.Wayland;

[TestFixture]
public class GlobalShortcutsPortalTests
{
    private static VariantValue Shortcuts(params (string Id, Dict<string, VariantValue> Options)[] shortcuts)
    {
        var array = new Array<Struct<string, Dict<string, VariantValue>>>();
        foreach (var (id, options) in shortcuts)
            array.Add(Struct.Create(id, options));
        return array;
    }

    private static Dict<string, VariantValue> Options(string? description, VariantValue? trigger)
    {
        var options = new Dict<string, VariantValue>();
        if (description != null) options.Add("description", description);
        if (trigger is { } t) options.Add("trigger_description", t);
        return options;
    }

    [Test]
    public void FromVariant_ReadsIdDescriptionAndTrigger()
    {
        var bound = GlobalShortcutsPortal.FromVariant(Shortcuts(
            ("ToggleCursorLock-0", Options("Lock the cursor to its screen", "Ctrl+Alt+Super+L")),
            ("LockSlaves-0", Options(null, null))));

        Assert.That(bound, Is.EqualTo((BoundShortcut[])
        [
            new BoundShortcut("ToggleCursorLock-0", "Lock the cursor to its screen", "Ctrl+Alt+Super+L"),
            new BoundShortcut("LockSlaves-0", null, null),
        ]));
    }

    [Test]
    public void FromVariant_ATriggerNotAString_IsNone()
    {
        var bound = GlobalShortcutsPortal.FromVariant(Shortcuts(("CopyFiles-0", Options("Copy files", 7u))));

        Assert.That(bound.Single().TriggerDescription, Is.Null);
    }

    [Test]
    public void FromVariant_Empty_IsNoShortcuts()
    {
        Assert.That(GlobalShortcutsPortal.FromVariant(Shortcuts()), Is.Empty);
    }

    [Test]
    public void Unassigned_AreThoseWithAnEmptyOrMissingTrigger()
    {
        // as xdg-desktop-portal-hyprland answers: every trigger_description empty
        var bound = GlobalShortcutsPortal.FromVariant(Shortcuts(
            ("ToggleCursorLock-0", Options("Lock the cursor to its screen", "")),
            ("LockSlaves-0", Options("Lock every slave", null)),
            ("CopyFiles-0", Options("Copy files", "Ctrl+Alt+Super+C"))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GlobalShortcutsPortal.Unassigned(bound).Select(s => s.Id), Is.EqualTo(["ToggleCursorLock-0", "LockSlaves-0"]));
            Assert.That(GlobalShortcutsPortal.Assigned(bound).Select(s => s.Id), Is.EqualTo(["CopyFiles-0"]));
        }
    }

    [Test]
    public void Assigned_CountsOnlyShortcutsWithATrigger()
    {
        BoundShortcut[] bound =
        [
            new BoundShortcut("a-0", "a", ""),
            new BoundShortcut("b-0", "b", null),
            new BoundShortcut("c-0", "c", "Scroll Lock"),
            new BoundShortcut("d-0", "d", "Ctrl+D"),
        ];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GlobalShortcutsPortal.Assigned(bound), Has.Count.EqualTo(2));
            Assert.That(GlobalShortcutsPortal.Assigned([]), Is.Empty);
            Assert.That(GlobalShortcutsPortal.Unassigned(bound), Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void UntriggeredWarning_WarnsOncePerOwner_AndAFreshOwnerWarnsAgain()
    {
        ShortcutRequest[] asked = [new ShortcutRequest("ToggleCursorLock-0", "Lock the cursor to its screen", "CTRL+ALT+LOGO+l")];
        BoundShortcut[] untriggered = [new BoundShortcut("ToggleCursorLock-0", "Lock the cursor to its screen", "")];
        var log = new CapturingLog<GlobalShortcutsPortalTests>();

        // one owner binding twice, as when the portal closes a session and a fresh one is bound
        var owner = new UntriggeredWarning();
        owner.Warn(asked, untriggered, log);
        owner.Warn(asked, untriggered, log);
        var afterOneOwner = log.Entries.Count(e => e.Level == LogLevel.Warning);
        new UntriggeredWarning().Warn(asked, untriggered, log);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterOneOwner, Is.EqualTo(1));
            Assert.That(log.Entries.Count(e => e.Level == LogLevel.Warning), Is.EqualTo(2));
            Assert.That(log.Messages[0], Does.StartWith("1 shortcuts have no trigger the portal knows of")
                .And.Contain("hl.bind(\"CTRL + ALT + SUPER + L\", hl.dsp.global(\"org.cathedral.Hydra:ToggleCursorLock-0\"))")
                .And.Contain("bind = CTRL ALT SUPER, l, global, org.cathedral.Hydra:ToggleCursorLock-0"));
        }
    }

    [Test]
    public void UntriggeredWarning_WithEveryShortcutTriggered_WarnsNothing_AndStillWarnsLater()
    {
        ShortcutRequest[] asked = [new ShortcutRequest("CopyFiles-0", "Copy files", "CTRL+ALT+LOGO+c")];
        var log = new CapturingLog<GlobalShortcutsPortalTests>();
        var owner = new UntriggeredWarning();

        owner.Warn(asked, [], log);
        var afterNone = log.Entries.Length;
        owner.Warn(asked, [new BoundShortcut("CopyFiles-0", "Copy files", null)], log);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterNone, Is.Zero);
            Assert.That(log.Entries.Count(e => e.Level == LogLevel.Warning), Is.EqualTo(1));
        }
    }
}
