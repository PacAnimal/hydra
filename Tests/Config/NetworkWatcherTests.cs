using Hydra.Config;
using Hydra.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Config;

[TestFixture]
public class NetworkWatcherTests
{
    private const string WorkSsid = "AttensiOffice";

    private static HydraConfig Work => new()
    {
        Mode = Mode.Slave,
        ProfileName = "Work",
        Conditions = new ConfigConditions { Ssid = WorkSsid, ScreenCount = 2, IsPluggedIn = true },
    };

    private static HydraConfig Roaming => new()
    {
        Mode = Mode.Slave,
        ProfileName = "Roaming",
        Conditions = new ConfigConditions { Ssid = WorkSsid, ScreenCount = 3 },
    };

    private sealed class FakeNetworkDetector : INetworkDetector
    {
        public List<string>? Ssids = [WorkSsid];
        public bool? PluggedIn = true;
        public Task<List<string>?> GetActiveSsids(CancellationToken cancel = default) => Task.FromResult(Ssids);
        public Task<bool?> GetIsPluggedIn(CancellationToken cancel = default) => Task.FromResult(PluggedIn);
    }

    private sealed class Harness
    {
        public readonly FakeNetworkDetector Detector = new();
        public readonly DormancyState Dormancy;
        public int Restarts;
        public int ScreenCount = 2;
        private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private readonly NetworkWatcher _watcher;

        // active is always the first profile — Resolve matches on reference, so it must be the same instance
        public Harness(HydraConfig active, params HydraConfig[] others)
        {
            Dormancy = new DormancyState(NullLogger<DormancyState>.Instance, () => _now);
            _watcher = new NetworkWatcher(Detector, () => ScreenCount, [active, .. others], active, null,
                Dormancy, NullLogger<NetworkWatcher>.Instance, () => Restarts++, () => _now);
        }

        public void Advance(TimeSpan by) => _now += by;

        // each check steps past the debounce window, so successive checks are all evaluated
        public Task Check()
        {
            Advance(TimeSpan.FromSeconds(30));
            return _watcher.TriggerCheck();
        }
    }

    [Test]
    public async Task DisplaysSleep_GoesDormant_WithoutRestarting()
    {
        var h = new Harness(Work)
        {
            ScreenCount = 1
        };
        await h.Check();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Dormancy.IsDormant, Is.True);
            Assert.That(h.Restarts, Is.Zero);
        }
    }

    // sleeping displays are the only mismatch our own input can undo — losing power means the machine was
    // unplugged and carried off, so it should drop off the relay at once
    [Test]
    public async Task PowerFlipsToBattery_Restarts_AndStaysAwake()
    {
        var h = new Harness(Work);
        h.Detector.PluggedIn = false;
        await h.Check();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Dormancy.IsDormant, Is.False);
            Assert.That(h.Restarts, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task ScreensLostAndPowerLostTogether_Restarts()
    {
        var h = new Harness(Work)
        {
            ScreenCount = 1
        };
        h.Detector.PluggedIn = false;
        await h.Check();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Dormancy.IsDormant, Is.False);
            Assert.That(h.Restarts, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task ProfileWithoutScreenCountCondition_NeverGoesDormant()
    {
        var home = new HydraConfig
        {
            Mode = Mode.Slave,
            ProfileName = "Home",
            Conditions = new ConfigConditions { Ssid = WorkSsid, IsPluggedIn = true },
        };
        var h = new Harness(home);
        h.Detector.PluggedIn = false;
        await h.Check();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Dormancy.IsDormant, Is.False);
            Assert.That(h.Restarts, Is.EqualTo(1));
        }
    }

    // more screens than the profile wants means someone opened the lid and is standing at the machine.
    // Inactivity only ever removes displays, so an increase is never the problem dormancy exists for.
    [Test]
    public async Task LidOpenedToThreeScreens_Restarts_AndStaysAwake()
    {
        var h = new Harness(Work)
        {
            ScreenCount = 3
        };
        await h.Check();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Dormancy.IsDormant, Is.False);
            Assert.That(h.Restarts, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task ConditionsReturn_WakesFromDormancy_WithoutRestarting()
    {
        var h = new Harness(Work)
        {
            ScreenCount = 1
        };
        await h.Check();
        Assert.That(h.Dormancy.IsDormant, Is.True);

        h.ScreenCount = 2;
        await h.Check();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Dormancy.IsDormant, Is.False);
            Assert.That(h.Restarts, Is.Zero);
        }
    }

    [Test]
    public async Task SsidChanged_Restarts_AndStaysAwake()
    {
        var h = new Harness(Work);
        h.Detector.Ssids = ["SomeCafe"];
        await h.Check();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Dormancy.IsDormant, Is.False);
            Assert.That(h.Restarts, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task SsidLostEntirely_Restarts_AndStaysAwake()
    {
        var h = new Harness(Work);
        h.Detector.Ssids = [];
        await h.Check();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Dormancy.IsDormant, Is.False);
            Assert.That(h.Restarts, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task WakingIntoADifferentProfile_Restarts()
    {
        var h = new Harness(Work, Roaming)
        {
            ScreenCount = 1
        };
        await h.Check();
        Assert.That(h.Dormancy.IsDormant, Is.True);

        // taken to a meeting and reopened: three screens now matches a different profile
        h.ScreenCount = 3;
        await h.Check();
        Assert.That(h.Restarts, Is.EqualTo(1));
    }

    // a woken machine that never got its profile back must leave the relay, so the master stops believing
    // its cursor is sitting on a live screen
    [Test]
    public async Task WakeDeadlineLapses_WhileStillDormant_Restarts()
    {
        var h = new Harness(Work)
        {
            ScreenCount = 1
        };
        await h.Check();
        Assert.That(h.Dormancy.RequestWake(), Is.True);

        h.Advance(DormancyState.WakeDeadline + TimeSpan.FromSeconds(1));
        await h.Dormancy.CheckWakeDeadline();
        Assert.That(h.Restarts, Is.EqualTo(1));
    }

    [Test]
    public async Task WakeDeadline_DoesNotLapseBeforeItIsDue()
    {
        var h = new Harness(Work)
        {
            ScreenCount = 1
        };
        await h.Check();
        h.Dormancy.RequestWake();

        h.Advance(DormancyState.WakeDeadline - TimeSpan.FromSeconds(1));
        await h.Dormancy.CheckWakeDeadline();
        Assert.That(h.Restarts, Is.Zero);
    }

    [Test]
    public async Task WakeThenConditionsReturn_CancelsTheDeadline()
    {
        var h = new Harness(Work)
        {
            ScreenCount = 1
        };
        await h.Check();
        h.Dormancy.RequestWake();

        h.ScreenCount = 2;
        await h.Check();
        h.Advance(DormancyState.WakeDeadline + TimeSpan.FromSeconds(1));
        await h.Dormancy.CheckWakeDeadline();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Dormancy.IsDormant, Is.False);
            Assert.That(h.Restarts, Is.Zero);
        }
    }

    // repeated wakes from an active master must not keep pushing the deadline out of reach
    [Test]
    public async Task RepeatedWakes_DoNotExtendTheDeadline()
    {
        var h = new Harness(Work)
        {
            ScreenCount = 1
        };
        await h.Check();
        Assert.That(h.Dormancy.RequestWake(), Is.True);

        h.Advance(TimeSpan.FromSeconds(20));
        Assert.That(h.Dormancy.RequestWake(), Is.False, "second wake must not re-arm");

        h.Advance(TimeSpan.FromSeconds(11));
        await h.Dormancy.CheckWakeDeadline();
        Assert.That(h.Restarts, Is.EqualTo(1));
    }

    // losing the screens a second time must start a fresh deadline, not inherit a spent one
    [Test]
    public async Task ScreensLostAgainAfterWaking_ReArmsTheDeadline()
    {
        var h = new Harness(Work)
        {
            ScreenCount = 1
        };
        await h.Check();
        Assert.That(h.Dormancy.RequestWake(), Is.True);

        h.ScreenCount = 2;
        await h.Check();
        Assert.That(h.Dormancy.IsDormant, Is.False);

        h.ScreenCount = 1;
        await h.Check();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Dormancy.IsDormant, Is.True);
            Assert.That(h.Dormancy.RequestWake(), Is.True, "a new dormant episode must arm afresh");
            Assert.That(h.Restarts, Is.Zero);
        }
    }

    [Test]
    public void WakeWhileAwake_IsIgnored()
    {
        var h = new Harness(Work);
        Assert.That(h.Dormancy.RequestWake(), Is.False);
    }

    [Test]
    public async Task MasterMode_DoesNotGoDormant()
    {
        var master = new HydraConfig
        {
            Mode = Mode.Master,
            ProfileName = "Work",
            Conditions = new ConfigConditions { Ssid = WorkSsid, ScreenCount = 2 },
        };
        var h = new Harness(master)
        {
            ScreenCount = 1
        };
        await h.Check();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Dormancy.IsDormant, Is.False);
            Assert.That(h.Restarts, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task NoScreensDetected_IsIgnoredEntirely()
    {
        var h = new Harness(Work)
        {
            ScreenCount = 0
        };
        await h.Check();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Dormancy.IsDormant, Is.False);
            Assert.That(h.Restarts, Is.Zero);
        }
    }

    [Test]
    public async Task SsidDetectionUnavailable_IsIgnoredEntirely()
    {
        var h = new Harness(Work);
        h.Detector.Ssids = null;
        h.ScreenCount = 1;
        await h.Check();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Dormancy.IsDormant, Is.False);
            Assert.That(h.Restarts, Is.Zero);
        }
    }
}
