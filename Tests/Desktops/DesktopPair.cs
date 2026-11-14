using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Tests.Setup;

namespace Tests.Desktops;

/// <summary>
/// A master desktop on the left of a slave desktop, each with a real Hydra and an input probe, and the user's hands on
/// the master. Each test starts with the cursor home, near the middle of the master's screen.
/// </summary>
public abstract class DesktopPair(DesktopKind masterKind, DesktopKind slaveKind)
{
    private const string Script = "/usr/local/bin/desktop.sh";
    private const string MasterName = "hm";
    private const string SlaveName = "hs";
    protected const int Step = 10;
    // ten times the widest screen desktop.sh makes by default, for a crossing that never comes
    private const int MaxSteps = 1000;
    // desktop.sh's default monitor, and the hyprland vm's virtio-gpu mode
    protected const int ScreenWidth = 1920;
    private const int ScreenHeight = 1080;
    // how far right homing ends from the left edge: well clear of the slave's edge even at 4x acceleration
    private const int HomeOffsetX = 100;
    // how long a batch at home waits for the master probe's motion: one into an edge moves nothing
    private static readonly TimeSpan HomePace = TimeSpan.FromMilliseconds(500);

    // the master's log lines this fixture follows
    protected const string EnteredHs = "Entered remote screen 'hs'";
    protected const string RemoteOnlyEnteredHs = "Remote-only: entered 'hs'";
    protected const string Returned = "Returned to local screen";
    private const string ReturnedOther = "returned to local screen";
    protected const string RelativeOn = "Mouse mode for 'hs': relative";
    protected const string RelativeOff = "Mouse mode for 'hs': absolute";
    // a wayland master's line once the compositor has released a capture: it stops emulating on the receiver as it
    // deactivates one, while the portal answers Release before acting
    protected static readonly Regex CaptureReleased = new(@"EIS emulation stopped: ", RegexOptions.CultureInvariant);
    // every mouse message the master sends, with debugMouse on; not the slave's "[mouse] delta from hm" lines it relays
    private static readonly Regex MouseSent = new(@"\[mouse\] (delta|move) to", RegexOptions.CultureInvariant);
    // a send, or a return home: a batch that takes the pointer home ends the sends it would wait for
    private static readonly Regex MouseSentOrReturned = new(
        $@"{MouseSent}|{Regex.Escape(Returned)}|{Regex.Escape(ReturnedOther)}", RegexOptions.CultureInvariant);

    private static int _fixtures;

    protected DesktopKind MasterKind { get; } = masterKind;
    protected DesktopKind SlaveKind { get; } = slaveKind;
    protected bool EvdevMaster => MasterKind == DesktopKind.Evdev;
    protected bool MasterIsWayland => MasterKind is DesktopKind.Gnome or DesktopKind.Kde or DesktopKind.Hyprland;
    protected bool X11Master => MasterKind is DesktopKind.Openbox or DesktopKind.KdeX11;
    protected bool X11Slave => SlaveKind is DesktopKind.Openbox or DesktopKind.KdeX11;

    // each is null until made, so a setup that failed half way stops only what it started
    private Desktop? _masterDesktop;
    private Desktop? _slaveDesktop;
    private Probe? _masterProbe;
    private Probe? _slaveProbe;
    private UserHands? _hands;
    private HydraInstance? _master;
    private HydraInstance? _slave;

    protected Desktop MasterDesktop => _masterDesktop!;
    protected Desktop SlaveDesktop => _slaveDesktop!;
    protected HydraInstance Master => _master!;
    protected HydraInstance Slave => _slave!;
    // an evdev master has no desktop, so no app to probe
    protected Probe? MasterProbe => _masterProbe;
    protected Probe SlaveProbe => _slaveProbe!;
    private protected UserHands Hands => _hands!;

    // why this fixture cannot run this pair, or null
    protected virtual string? Unsupported() => null;

    [OneTimeSetUp]
    public async Task StartPair()
    {
        if (!File.Exists(Script))
            Assert.Ignore($"{Script} is not installed; the desktop tests run in the desktop lane's container");
        if (!Desktop.Available(MasterKind) || !Desktop.Available(SlaveKind))
            Assert.Ignore($"this lane has no {Desktop.Arg(MasterKind)} and {Desktop.Arg(SlaveKind)} desktops");
        if (Unsupported() is { } why)
            Assert.Ignore(why);

        var port = 5700 + Interlocked.Increment(ref _fixtures);

        _masterDesktop = await Desktop.Start(MasterKind, "m-" + Desktop.Arg(MasterKind));
        _slaveDesktop = await Desktop.Start(SlaveKind, "s-" + Desktop.Arg(SlaveKind));
        if (!EvdevMaster)
            _masterProbe = await Probe.Input(_masterDesktop);
        _slaveProbe = await Probe.Input(_slaveDesktop);

        // the hands come first on evdev and hyprland, so the master finds their devices as it starts, as a user's
        // keyboard is there before hydra (a keyboard plugged in later is the key source's own tests' business)
        if (EvdevMaster)
            _hands = await UserHands.Attach(_masterDesktop, TestLog.Factory.CreateLogger<UserHands>(), PaceEvdev);
        else if (MasterKind == DesktopKind.Hyprland)
            _hands = await UserHands.Attach(_masterDesktop, TestLog.Factory.CreateLogger<UserHands>(), PaceHyprland);

        _master = await HydraInstance.Start(_masterDesktop, MasterName,
            HydraInstance.MasterConfig(MasterName, SlaveName, port, remoteOnly: EvdevMaster));
        // the slave dials the master's embedded relay; started before it listens, its first connect is refused and its
        // retry waits a whole reconnect delay, which a cold desktop can push past the setup's bound
        await Master.WaitForLog("Embedded Styx relay listening");
        _slave = await HydraInstance.Start(_slaveDesktop, SlaveName, HydraInstance.SlaveConfig(SlaveName, port));

        await Master.WaitForLog("Screen info from hs");
        // the wayland handler arms the slave's edge as a pointer barrier once it knows the slave's screen
        if (_masterDesktop.IsWayland)
            await Master.WaitForLog("Pointer barriers set: Right");
        // a remote-only master enters the slave as soon as its screen is known
        if (EvdevMaster)
            await Master.WaitForLog(RemoteOnlyEnteredHs);

        if (!EvdevMaster && MasterKind != DesktopKind.Hyprland)
            _hands = await UserHands.Attach(_masterDesktop, TestLog.Factory.CreateLogger<UserHands>());

        // the compositor's pointer may rest on the armed edge already, so the first test must not be the first to move it
        await Home();
    }

    [OneTimeTearDown]
    public async Task StopPair()
    {
        // the reverse of the start, each tried whatever the one before it did
        var errors = new List<Exception>();
        await Stop(errors, _hands);
        await Stop(errors, _slave);
        await Stop(errors, _master);
        await Stop(errors, _slaveProbe);
        await Stop(errors, _masterProbe);
        await Stop(errors, _slaveDesktop);
        await Stop(errors, _masterDesktop);
        _hands = null;
        _slave = null;
        _master = null;
        _slaveProbe = null;
        _masterProbe = null;
        _slaveDesktop = null;
        _masterDesktop = null;
        if (errors.Count > 0)
            throw new AggregateException("the desktop pair did not stop cleanly", errors);
    }

    // a failed test's own output carries both hydras' latest lines: the fixture's teardown dump lands in no test's
    [TearDown]
    public async Task DumpOnFailure()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status != NUnit.Framework.Interfaces.TestStatus.Failed)
            return;
        foreach (var hydra in new[] { _master, _slave })
        {
            if (hydra == null)
                continue;
            await TestContext.Out.WriteLineAsync($"--- hydra '{hydra.Name}' log at the failure (last 150 lines) ---");
            foreach (var line in hydra.Log.TakeLast(150))
                await TestContext.Out.WriteLineAsync(line);
        }
    }

    [SetUp]
    public async Task BringCursorHome()
    {
        // relative mode a failed test left on would hold the cursor on hs
        if (Master.LastIndexOf(RelativeOn) > Master.LastIndexOf(RelativeOff))
        {
            if (!Visiting())
                await CrossRight();
            await ToggleRelative(RelativeOff);
        }

        await Home();

        // a command's reply comes after every event the app already had, so the queues start clean from here
        if (MasterProbe != null)
            await Settle(MasterProbe);
        await Settle(SlaveProbe);
    }

    // the cursor at the master's centre, by passes that end there and enter nothing; an evdev master has no screen of
    // its own, so it stays on hs
    private async Task Home()
    {
        if (EvdevMaster)
            return;
        var bound = Stopwatch.StartNew();
        var passes = 0;
        while (true)
        {
            if (Visiting())
            {
                await ReturnLeft();
                // the next test starts only once the compositor has the pointer back
                if (MasterIsWayland)
                    await WaitForCaptureReleased();
            }
            var from = Master.Log.Count;
            passes++;
            await HomingPass();
            // a capture the pass began becomes a visit, and its entry is logged
            if (Master.IndexOf(EnteredHs, from) < 0 && !Visiting())
                return;
            if (bound.Elapsed >= Bound)
                Assert.Fail($"the cursor did not come home in {passes} passes over {Bound}: every pass entered hs; " +
                            $"last master lines:\n{string.Join('\n', Master.Log.TakeLast(20))}");
        }
    }

    // left and up into the top-left corner, never right first, then down and barely right: compositors accelerate
    // relative motion (mutter up to ~4x at these step sizes), so any long step right can reach the slave's edge
    private async Task HomingPass()
    {
        for (var i = 0; i < 4; i++)
            await Hands.MoveBy(-ScreenWidth / 2, -ScreenHeight / 2);
        await Hands.MoveBy(HomeOffsetX, ScreenHeight / 4);
    }

    // clicks the left button and waits for the probe's press and release; the press, whose "at" is where the pointer was
    protected async Task<JsonElement> ClickOn(Probe probe)
    {
        await Hands.Button(Keys.BtnLeft, true);
        await Hands.Button(Keys.BtnLeft, false);
        var press = await probe.WaitFor(e => IsButton(e, 1, true));
        await probe.WaitFor(e => IsButton(e, 1, false));
        return press;
    }

    protected static bool IsButton(JsonElement e, int button, bool pressed) =>
        Probe.Has(e, "button") && e.GetProperty("button").GetInt32() == button && e.GetProperty("pressed").GetBoolean() == pressed;

    // after a batch of the evdev hands, a mouse message the master sends from then on, or its return home: its event
    // loop drains the device each time it reads, so at most this batch is left unread when the next is written, and two
    // fit the kernel's buffer. remote-only never crosses home, but a disconnect or lost capture sends it there, where
    // hydra logs no motion
    private Func<Task> PaceEvdev()
    {
        var master = _master;
        if (master == null)
            return () => Task.CompletedTask;
        var from = master.Log.Count;
        return () => master.WaitForLog(MouseSentOrReturned, from);
    }

    // after a batch of the hyprland hands on a visit, the master's next mouse message or its return home, as on evdev:
    // the batch itself can cross home, and no send follows. at home hydra logs no motion, so the master probe's next
    // motion line paces instead, bounded and never failing: a batch pushed into an edge or corner moves nothing
    private Func<Task> PaceHyprland()
    {
        var master = _master;
        if (master == null)
            return () => Task.CompletedTask;
        // start taken before the visit check, so a return logged in between is still at or after it
        var from = master.Log.Count;
        if (!Visiting())
            return _masterProbe?.Watch(e => Probe.Has(e, "motion"), HomePace) ?? (() => Task.CompletedTask);
        return () => master.WaitForLog(MouseSentOrReturned, from);
    }

    private static async Task Settle(Probe probe)
    {
        await probe.Command(new { formats = true }, "formats");
        probe.Drain();
    }

    // whether the master's last entry to hs is later than its last return home
    protected bool Visiting() =>
        Math.Max(Master.LastIndexOf(EnteredHs), Master.LastIndexOf(RemoteOnlyEnteredHs)) > LastReturn();

    private int LastReturn() =>
        Math.Max(Master.LastIndexOf(Returned), Master.LastIndexOf(ReturnedOther));

    // a wayland master's capture release after its last visit: searched from that visit's entry, since hyprland's
    // master logs the release before its return line; nothing to wait for if it never entered hs
    protected async Task WaitForCaptureReleased()
    {
        var entered = Master.LastIndexOf(EnteredHs);
        if (entered >= 0)
            await Master.WaitForLog(CaptureReleased, entered);
    }

    // moves right until the master enters hs; the index of its entry line, the first after since (default: now)
    protected async Task<int> CrossRight(int? since = null)
    {
        // a remote-only master has no edge to cross: it entered hs as it started, and stays there
        if (EvdevMaster)
        {
            Assert.That(Visiting(), Is.True, "the remote-only master is not on hs");
            return Master.LastIndexOf(RemoteOnlyEnteredHs);
        }
        return await MoveUntil(Step, EnteredHs, since ?? Master.Log.Count);
    }

    // moves left until the master returns to its own screen; the index of its return line
    protected async Task<int> ReturnLeft()
    {
        if (EvdevMaster)
            throw new InvalidOperationException("a remote-only master has no screen of its own to return to");
        return await MoveUntil(-Step, Returned, Master.Log.Count);
    }

    // taps the relative mouse hotkey on the master; the index of its mode line
    protected async Task<int> ToggleRelative(string expected)
    {
        var from = Master.Log.Count;
        await Hands.Tap(Keys.RelativeMouseHotkey);
        return await Master.WaitForLog(expected, from);
    }

    protected async Task<int> MoveUntil(int dx, string fragment, int from, int maxSteps = MaxSteps)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            var index = Master.IndexOf(fragment, from);
            if (index >= 0)
                return index;
            await Hands.MoveBy(dx, 0);
        }
        // every move is out; the master may still be working through them
        return await Master.WaitForLog(fragment, from);
    }

    private static async Task Stop(List<Exception> errors, IAsyncDisposable? resource)
    {
        try
        {
            if (resource != null)
                await resource.DisposeAsync();
        }
        catch (Exception e)
        {
            errors.Add(e);
        }
    }
}

// the master and slave desktops a DesktopPair fixture runs on
public static class DesktopPairs
{
    public static IEnumerable<TestFixtureData> All()
    {
        DesktopKind[] masters = [DesktopKind.Openbox, DesktopKind.Gnome, DesktopKind.Kde, DesktopKind.KdeX11, DesktopKind.Evdev, DesktopKind.Hyprland];
        DesktopKind[] slaves = [DesktopKind.Openbox, DesktopKind.Gnome, DesktopKind.Kde, DesktopKind.KdeX11, DesktopKind.Hyprland];
        foreach (var master in masters)
        {
            foreach (var slave in slaves)
            {
                // kde and kde-x11 come from different images, so never run side by side
                var kinds = new[] { master, slave };
                if (kinds.Contains(DesktopKind.Kde) && kinds.Contains(DesktopKind.KdeX11))
                    continue;
                // hyprland's image has only openbox and evdev beside it
                if (kinds.Contains(DesktopKind.Hyprland) && kinds.Any(k => k is not (DesktopKind.Hyprland or DesktopKind.Openbox or DesktopKind.Evdev)))
                    continue;
                // and hyprland owns the vm's one kms device, so never runs twice
                if (master == DesktopKind.Hyprland && slave == DesktopKind.Hyprland)
                    continue;
                yield return new TestFixtureData(master, slave);
            }
        }
    }
}
