using System.Text.Json;

namespace Tests.Desktops;

[Category("Desktops")]
[TestFixtureSource(typeof(DesktopPairs), nameof(DesktopPairs.All))]
public sealed class CrossingTests(DesktopKind master, DesktopKind slave) : DesktopPair(master, slave)
{
    [Test]
    public async Task Mouse_CrossesRight_AppSeesEnterAndMotion()
    {
        // motion is no signal: gtk hands it over on its frame clock, which an idle window on headless kwin never ticks, so
        // it waits for the next other event. a click is delivered at once, and carries where the pointer is. nor is the
        // landing x known: the steps right until the entry is decided are carried over too, so only y is compared
        SlaveProbe.Drain();
        await CrossRight();

        var first = await ClickOn(SlaveProbe);
        await Hands.MoveBy(0, 50);
        var second = await ClickOn(SlaveProbe);

        Assert.That(At(second)[1], Is.GreaterThan(At(first)[1]), "the move down did not reach the slave's app");
    }

    [Test]
    public async Task Mouse_ReturnsLeft()
    {
        if (EvdevMaster)
            Assert.Ignore("a remote-only master has no screen of its own to return to");

        var entered = await CrossRight();
        MasterProbe!.Drain();

        var returned = await ReturnLeft();
        Assert.That(returned, Is.GreaterThan(entered));
        // a wayland master's pointer is the compositor's again only once it has released the capture
        if (MasterIsWayland)
            await Master.WaitForLog(CaptureReleased, entered);

        // a click home reaches the master's app, which proves the pointer is home and the master's apps get input again
        await Hands.MoveBy(-20, 0);
        SlaveProbe.Drain();
        await ClickOn(MasterProbe);

        // and nothing of it reached the slave: a click made back there is the first button its app sees
        await CrossRight();
        var buttons = new List<JsonElement>();
        await Hands.Button(Keys.BtnLeft, true);
        await Hands.Button(Keys.BtnLeft, false);
        await SlaveProbe.WaitFor(e =>
        {
            if (Probe.Has(e, "button"))
                buttons.Add(e);
            return IsButton(e, 1, false);
        });
        Assert.That(buttons, Has.Count.EqualTo(2), "the slave's app saw the click made home: " + string.Join(", ", buttons));
    }

    private static double[] At(JsonElement button) =>
        [button.GetProperty("at")[0].GetDouble(), button.GetProperty("at")[1].GetDouble()];

    [Test]
    public async Task Keys_Typed_OnMaster_ArriveAsCharacters()
    {
        const string text = "hello world";
        await CrossRight();

        await Hands.Type(text);

        var typed = "";
        while (typed.Length < text.Length)
            typed += Char(await NextTyped());
        Assert.That(typed, Is.EqualTo(text));
    }

    [Test]
    public async Task Shifted_And_Symbols()
    {
        await CrossRight();

        await Hands.Tap(Keys.KeyLeftShift, Keys.KeyA);
        Assert.That(Char(await NextTyped()), Is.EqualTo("A"));

        await Hands.Tap(Keys.KeyLeftShift, Keys.Key2);
        Assert.That(Char(await NextTyped()), Is.EqualTo("@"));
    }

    [Test]
    public async Task Buttons_And_Scroll()
    {
        await CrossRight();

        await Hands.Button(Keys.BtnLeft, true);
        await SlaveProbe.WaitFor(e => IsButton(e, 1, true));
        await Hands.Button(Keys.BtnLeft, false);
        await SlaveProbe.WaitFor(e => IsButton(e, 1, false));

        // a click after the scroll is the sentinel: every scroll event reaches the probe before its release
        await Hands.Scroll(3);
        await Hands.Button(Keys.BtnLeft, true);
        await Hands.Button(Keys.BtnLeft, false);
        var seen = new List<JsonElement>();
        await SlaveProbe.WaitFor(e =>
        {
            seen.Add(e);
            return IsButton(e, 1, false);
        });

        var dys = seen.Where(e => Probe.Has(e, "scroll")).Select(e => e.GetProperty("scroll")[1].GetDouble()).ToList();
        Assert.That(dys, Is.Not.Empty, "no scroll reached the slave's app");
        Assert.That(dys.Sum(), Is.GreaterThan(0), "the scroll went up, not down");
    }

    [Test]
    public async Task HeldButton_RefusesEntry()
    {
        if (EvdevMaster)
            Assert.Ignore("a remote-only master has no screen of its own, so no edge to refuse at");
        try
        {
            if (MasterKind == DesktopKind.Hyprland)
                await HeldButton_ReleaseEndsVisit();
            else if (MasterIsWayland)
                await HeldButton_ReleaseNotForwarded();
            else
                await HeldButton_EntryRefused();
        }
        finally
        {
            await ReleaseAtHome();
        }
    }

    // kwin keeps a button down after a drag into a barrier, which would hold every later test's crossing: it goes up at
    // home, and a click there ends what the master's desktop may still think is held. the master's app sees the
    // release either way; a press only if nothing was held
    private async Task ReleaseAtHome()
    {
        if (Visiting())
            await ReturnLeft();
        if (MasterIsWayland)
            await WaitForCaptureReleased();
        await Hands.Button(Keys.BtnLeft, false);
        await Hands.Button(Keys.BtnLeft, true);
        await Hands.Button(Keys.BtnLeft, false);
        await MasterProbe!.WaitFor(e => IsButton(e, 1, false));
    }

    private async Task HeldButton_EntryRefused()
    {
        var from = Master.Log.Count;
        await Hands.Button(Keys.BtnLeft, true);
        // from the middle, well past the right edge
        await Hands.MoveBy(1920, 0);

        // the cursor lock hotkey twice is the sentinel: its lines come after every move before it, and leave the lock as it was
        await Hands.Tap(Keys.CursorLockHotkey);
        await Hands.Tap(Keys.CursorLockHotkey);
        var sentinel = await Master.WaitForLog("Screen lock: unlocked", from);
        Assert.That(Master.IndexOf(EnteredHs, from), Is.EqualTo(-1).Or.GreaterThan(sentinel),
            "the master entered hs with the left button held");

        // the release may already let a move through, so the entry is looked for from the sentinel on
        await Hands.Button(Keys.BtnLeft, false);
        var entered = await CrossRight(sentinel);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Master.IndexOf(EnteredHs, from), Is.EqualTo(entered), "the first entry is the one after the release");
            Assert.That(entered, Is.GreaterThan(sentinel));
        }
    }

    // a wayland master cannot see a button held at home, since input capture reports only what it captured, so it enters;
    // the release then arrives with no press in that capture, and is dropped rather than sent to hs
    private async Task HeldButton_ReleaseNotForwarded()
    {
        await Hands.Button(Keys.BtnLeft, true);
        await CrossRight();
        await Hands.Button(Keys.BtnLeft, false);

        // a click after the drag is the sentinel: anything the drag sent reaches the app before it
        await Hands.Button(Keys.BtnLeft, true);
        await Hands.Button(Keys.BtnLeft, false);
        var buttons = new List<JsonElement>();
        await SlaveProbe.WaitFor(e =>
        {
            if (Probe.Has(e, "button"))
                buttons.Add(e);
            return IsButton(e, 1, false);
        });

        // a button the compositor reports held as the capture starts carries the drag across, press and release; either
        // way every release has its press before it
        var seen = string.Join(", ", buttons);
        Assert.That(buttons.Count % 2, Is.Zero, "hs saw an unpaired button: " + seen);
        for (var i = 0; i < buttons.Count; i += 2)
        {
            Assert.That(IsButton(buttons[i], 1, true) && IsButton(buttons[i + 1], 1, false), Is.True,
                "hs saw a release with no press before it: " + seen);
        }
    }

    // xdph sends nothing more after the drag's release, so a hyprland master ends the visit on it: home by the barrier,
    // nothing sent to hs, and the edge crosses again
    private async Task HeldButton_ReleaseEndsVisit()
    {
        await Hands.Button(Keys.BtnLeft, true);
        var entered = await CrossRight();
        await Hands.Button(Keys.BtnLeft, false);
        await Master.WaitForLog(Returned, entered);
        await WaitForCaptureReleased();

        await CrossRight();
        // a click on hs is the sentinel: anything the drag sent reaches the app before it
        await Hands.Button(Keys.BtnLeft, true);
        await Hands.Button(Keys.BtnLeft, false);
        var buttons = new List<JsonElement>();
        await SlaveProbe.WaitFor(e =>
        {
            if (Probe.Has(e, "button"))
                buttons.Add(e);
            return IsButton(e, 1, false);
        });

        var seen = string.Join(", ", buttons);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buttons, Has.Count.EqualTo(2), "hs saw a button from the drag: " + seen);
            Assert.That(IsButton(buttons[0], 1, true), Is.True, "hs saw a button from the drag: " + seen);
        }
    }

    // the next pressed key the slave's app read as a character; modifiers alone carry none
    private Task<JsonElement> NextTyped() =>
        SlaveProbe.WaitFor(e => Probe.Has(e, "key") && e.GetProperty("pressed").GetBoolean() && Char(e).Length > 0);

    private static string Char(JsonElement key) => key.GetProperty("char").GetString() ?? "";
}
