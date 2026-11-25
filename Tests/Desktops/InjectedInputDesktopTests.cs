namespace Tests.Desktops;

// A VNC server on an X11 master's display injects through XTest, and so do the x11 hands: with injectedInputReturnsHome
// on, the hands' pointer is someone at the master, which never crosses to the slave and ends a visit where it points.
// So the visit begins by the monitor switch, whose keys stay the user's own
[Category("Desktops")]
[TestFixture(DesktopKind.Openbox, DesktopKind.Openbox)]
public sealed class InjectedInputDesktopTests(DesktopKind master, DesktopKind slave) : DesktopPair(master, slave)
{
    private const string InjectedReturn = "input was injected on this display";
    private const string SwitchedToHs = "\u2192 'hs'";

    protected override bool InjectedInputReturnsHome => true;

    [Test]
    public async Task InjectedMotion_OnAVisit_ReturnsHome_AndAClickThenLandsHome()
    {
        var from = Master.Log.Count;
        await Hands.Tap(Keys.SwitchRightHotkey);
        var entered = await Master.WaitForLog(SwitchedToHs, from);
        SlaveProbe.Drain();

        await Hands.MoveBy(0, 20);
        await Master.WaitForLog(InjectedReturn, entered);

        await ClickOn(MasterProbe!);
        Assert.That(SlaveProbe.Drain().Where(e => Probe.Has(e, "button")), Is.Empty, "a click reached the slave's app");
    }

    [Test]
    public async Task InjectedMotion_AtHome_NeverCrossesTheEdge()
    {
        var from = Master.Log.Count;
        // well past the slave's edge: the pointer stops there
        for (var i = 0; i < 2 * ScreenWidth / Step; i++)
            await Hands.MoveBy(Step, 0);

        // a click marks that the master has had every move: it reaches the master's own app
        await ClickOn(MasterProbe!);
        Assert.That(Master.IndexOf(EnteredHs, from), Is.Negative, "the injected pointer crossed to the slave");
    }
}
