namespace Tests.Desktops;

[Category("Desktops")]
[TestFixtureSource(typeof(DesktopPairs), nameof(DesktopPairs.All))]
public sealed class RelativeMouseTests(DesktopKind master, DesktopKind slave) : DesktopPair(master, slave)
{
    private const int Steps = 40;
    private const int StepDx = -20;
    // the default relativeMouseScale
    private const double Scale = 1.0;
    // the master's debug line for the left button forwarded to hs: it is handled after every move before it
    private const string LeftUp = "Mouse: up Left";

    [Test]
    public async Task Game_TurnRate_IsConstant_InRelativeMode()
    {
        await CrossRight();
        await using var game = await Probe.Game(SlaveDesktop);
        Assert.That(game.Ready.GetProperty("relative").GetBoolean(), Is.True);
        // mutter gives the game the pointer ~250 ms after SDL says ready; until then a click lands on the probe beneath
        await SlaveProbe.WaitFor(e => Probe.Has(e, "leave"));

        var toggled = await ToggleRelative(RelativeOn);
        await StartLine(game);
        for (var i = 0; i < Steps; i++)
            await Hands.MoveBy(StepDx, 0);

        // a click is the sentinel: the game reads it after every motion before it
        await Hands.Button(Keys.BtnLeft, true);
        await Hands.Button(Keys.BtnLeft, false);
        var rels = await RelsUntilRelease(game);
        var handled = await Master.WaitForLog(LeftUp, toggled);

        var expected = Steps * StepDx * Scale;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rels.Sum(), Is.EqualTo(expected).Within(Math.Abs(expected) * 0.05), "the turn is not what the hands moved");
            Assert.That(rels, Has.All.LessThanOrEqualTo(0), "the turn went back right");
            Assert.That(Master.IndexOf(Returned, toggled), Is.EqualTo(-1).Or.GreaterThan(handled),
                "the master left hs though relative mode never crosses an edge");
        }

        await ToggleRelative(RelativeOff);
    }

    // the old behaviour, for contrast: an absolute cursor turning left leaves hs at its left edge. it moves until that
    // edge from wherever the crossing left it (an unpaced home carries it deep into hs), at most a screen and a margin
    [Test]
    public async Task Game_AbsoluteMode_Diverges()
    {
        if (!X11Slave)
            Assert.Ignore("an absolute pointer only drives a game's relative mode on an x11 slave");
        if (EvdevMaster)
            Assert.Ignore("a remote-only master has no screen to return to, so no edge ends its visit");

        var entered = await CrossRight();
        await using var game = await Probe.Game(SlaveDesktop);
        Assert.That(game.Ready.GetProperty("relative").GetBoolean(), Is.True);
        // mutter gives the game the pointer ~250 ms after SDL says ready; until then a click lands on the probe beneath
        await SlaveProbe.WaitFor(e => Probe.Has(e, "leave"));
        await StartLine(game);

        var returned = await MoveUntil(StepDx, Returned, entered, ScreenWidth / -StepDx + Steps);
        Assert.That(returned, Is.GreaterThan(entered), "the visit did not end at hs's left edge");
    }

    // a click in the game focuses it and is the start line: what the game read before its release is not measured
    private async Task StartLine(Probe game)
    {
        await Hands.Button(Keys.BtnLeft, true);
        await Hands.Button(Keys.BtnLeft, false);
        await game.WaitFor(e => IsButton(e, 1, false));
    }

    // the x of every motion the game reads up to the release of its left button
    private static async Task<List<double>> RelsUntilRelease(Probe game)
    {
        var rels = new List<double>();
        await game.WaitFor(e =>
        {
            if (Probe.Has(e, "rel"))
                rels.Add(e.GetProperty("rel")[0].GetDouble());
            return IsButton(e, 1, false);
        });
        return rels;
    }
}
