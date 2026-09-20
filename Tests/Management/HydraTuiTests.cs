using Hydra.Management;
using Hydra.Tui;

namespace Tests.Management;

public class HydraTuiTests
{
    [Test]
    public void RestartCompletionDetectsWindowsProcessReplacement()
    {
        var previous = Status(processId: 10, uptime: 120);
        var current = Status(processId: 11, uptime: 1);

        Assert.That(HydraTui.HasRestarted(previous, current), Is.True);
    }

    [Test]
    public void RestartCompletionDetectsUnixExecByResetUptime()
    {
        var previous = Status(processId: 10, uptime: 120);
        var current = Status(processId: 10, uptime: 1);

        Assert.That(HydraTui.HasRestarted(previous, current), Is.True);
    }

    [Test]
    public void RestartCompletionRejectsOrdinaryStatusRefresh()
    {
        var previous = Status(processId: 10, uptime: 120);
        var current = Status(processId: 10, uptime: 121);

        Assert.That(HydraTui.HasRestarted(previous, current), Is.False);
    }

    [Test]
    public void StartIsAvailableAfterAConfirmedShutdown()
    {
        Assert.That(HydraTui.CanStartHydra(
            connected: false, shutdownConfirmed: true, commandBusy: false), Is.True);
    }

    [Test]
    public void StartIsUnavailableForAnUnconfirmedManagementFailure()
    {
        Assert.That(HydraTui.CanStartHydra(
            connected: false, shutdownConfirmed: false, commandBusy: false), Is.False);
    }

    [Test]
    public void StartIsUnavailableWhileConnectedOrBusy()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(HydraTui.CanStartHydra(
                connected: true, shutdownConfirmed: true, commandBusy: false), Is.False);
            Assert.That(HydraTui.CanStartHydra(
                connected: false, shutdownConfirmed: true, commandBusy: true), Is.False);
        }
    }

    [Test]
    public void RelayReconnectCompletionRequiresANewerConnectionAttempt()
    {
        var previous = Status(processId: 10, uptime: 120, relayAttempts: 3);
        var oldConnection = Status(processId: 10, uptime: 121, relayAttempts: 3);
        var newConnection = Status(processId: 10, uptime: 122, relayAttempts: 4);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(HydraTui.HasRelayReconnected(previous, oldConnection), Is.False);
            Assert.That(HydraTui.HasRelayReconnected(previous, newConnection), Is.True);
        }
    }

    [Test]
    public void RelayReconnectCompletionTreatsNoPriorStatusAsAFreshConnection()
    {
        // The very first status poll after opening the TUI has nothing to compare against —
        // a live connection at that point is still a completion worth surfacing, not a no-op.
        var current = Status(processId: 10, uptime: 5, relayAttempts: 1);

        Assert.That(HydraTui.HasRelayReconnected(previous: null, current), Is.True);
    }

    [Test]
    public void RelayReconnectCompletionIsFalseWhenCurrentlyDisconnected()
    {
        var previous = Status(processId: 10, uptime: 120, relayAttempts: 3);
        var stillDisconnected = Status(processId: 10, uptime: 121); // relayAttempts: null → RelayConnected: false

        Assert.That(HydraTui.HasRelayReconnected(previous, stillDisconnected), Is.False);
    }

    [Test]
    public void OverviewShowsUnavailableNetworkWhenRelayHasNeverConnected()
    {
        var status = Status(processId: 10, uptime: 30);

        var overview = HydraTui.TuiController.FormatOverview(status);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(overview, Does.Contain("Network       unavailable"));
            Assert.That(overview, Does.Contain("Relay state   disconnected"));
        }
    }

    [Test]
    public void OverviewFallsBackGracefullyWhenOptionalDiagnosticsAreMissing()
    {
        // ActiveNetworkAdapters/EmbeddedRelayPeers/PeerLatency are nullable specifically because
        // an older daemon's status response won't have them — this is the exact shape a newer
        // TUI sees when talking to one, and it must render sensible fallback text, not throw.
        var status = Status(processId: 10, uptime: 30, relayAttempts: 1) with
        {
            ActiveNetworkAdapters = null,
            EmbeddedRelayPeers = null,
            PeerLatency = null,
        };

        var overview = HydraTui.TuiController.FormatOverview(status);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(overview, Does.Contain("(none detected)"));
            Assert.That(overview, Does.Contain("(not hosting an embedded relay)"));
            Assert.That(overview, Does.Contain("(collecting samples)"));
        }
    }

    [Test]
    public void OverviewReportsDormantAndUnroutedState()
    {
        var status = Status(processId: 10, uptime: 30) with { Dormant = true };

        var overview = HydraTui.TuiController.FormatOverview(status);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(overview, Does.Contain("Dormant       yes"));
            Assert.That(overview, Does.Contain("Active route  n/a"));
        }
    }

    [Test]
    public void OverviewShowsRemoteRouteWhenRouterIsActive()
    {
        var status = Status(processId: 10, uptime: 30) with
        {
            Router = new RouterStatus(IsRemote: true, ActiveHost: "laptop", ActiveScreen: "laptop",
                LockedToScreen: true, ConfinedToScreen: false, RelativeMouse: true),
        };

        var overview = HydraTui.TuiController.FormatOverview(status);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(overview, Does.Contain("Active route  laptop/laptop"));
            Assert.That(overview, Does.Contain("Screen lock   locked"));
            Assert.That(overview, Does.Contain("Mouse mode    relative"));
        }
    }

    [Test]
    public void PeersViewReportsNoneDetectedAndNoPeersOnlineWhenEmpty()
    {
        var status = Status(processId: 10, uptime: 30);

        var peers = HydraTui.TuiController.FormatPeers(status);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers, Does.Contain("(none detected)"));
            Assert.That(peers, Does.Contain("(no peers online)"));
        }
    }

    [Test]
    public void PeersViewListsEveryScreenOnAConnectedPeer()
    {
        var status = Status(processId: 10, uptime: 30) with
        {
            Peers =
            [
                new PeerStatus("laptop", "MacOS", Connected: true,
                [
                    new ScreenStatus("laptop-built-in", "laptop", 2560, 1600, 1.0m, null),
                    new ScreenStatus("laptop-external", "laptop", 3840, 2160, 1.0m, 1.0m),
                ]),
            ],
        };

        var peers = HydraTui.TuiController.FormatPeers(status);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers, Does.Contain("laptop-built-in"));
            Assert.That(peers, Does.Contain("laptop-external"));
            Assert.That(peers, Does.Contain("● laptop"));
        }
    }

    [Test]
    public void PeersViewMarksADisconnectedPeerDistinctlyFromAConnectedOne()
    {
        var status = Status(processId: 10, uptime: 30) with
        {
            Peers = [new PeerStatus("laptop", "MacOS", Connected: false, [])],
        };

        var peers = HydraTui.TuiController.FormatPeers(status);

        Assert.That(peers, Does.Contain("○ laptop"));
    }

    [Test]
    public void DiagnosticsReportsUnavailableAndUnknownWhenNothingHasEverConnected()
    {
        // The very first Diagnostics render, before any status poll has ever succeeded — every
        // optional field must fall back gracefully rather than throw on a null revision/snapshot.
        var diagnostics = HydraTui.TuiController.FormatDiagnostics(
            connected: false, configPath: "/etc/hydra.conf", configRevision: null, lastSnapshot: null, logCursor: 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnostics, Does.Contain("Management     unavailable"));
            Assert.That(diagnostics, Does.Contain("Config rev     unknown"));
            Assert.That(diagnostics, Does.Contain("Last snapshot  none"));
            Assert.That(diagnostics, Does.Contain("Last error     none"));
        }
    }

    [Test]
    public void DiagnosticsReportsConnectedStateAndTheTriggeringError()
    {
        var snapshot = DateTimeOffset.UtcNow;

        var diagnostics = HydraTui.TuiController.FormatDiagnostics(
            connected: true, configPath: "/etc/hydra.conf", configRevision: "rev-7",
            lastSnapshot: snapshot, logCursor: 42, error: new InvalidOperationException("relay unreachable"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnostics, Does.Contain("Management     connected"));
            Assert.That(diagnostics, Does.Contain("Config rev     rev-7"));
            Assert.That(diagnostics, Does.Contain("Log cursor     42"));
            Assert.That(diagnostics, Does.Contain("Last error     relay unreachable"));
        }
    }

    [Test]
    public void ShortCategoryLeavesACategoryThatAlreadyFitsUnchanged()
    {
        Assert.That(HydraTui.TuiController.ShortCategory("Hydra.Config"), Is.EqualTo("Hydra.Config"));
    }

    [Test]
    public void ShortCategoryDropsWholeNamespaceSegmentsRatherThanCuttingMidWord()
    {
        // The namespace prefix is the least useful part of a long category name — but dropping it a
        // raw character at a time can slice through a word (e.g. "ra.Relay.RelayConnection", missing
        // "Hyd"). It must drop whole leading segments instead, so the result always reads cleanly.
        var truncated = HydraTui.TuiController.ShortCategory("Hydra.Platform.MacOs.MacScreenDetector");

        Assert.That(truncated, Is.EqualTo("MacOs.MacScreenDetector"));
    }

    [Test]
    public void ShortCategoryFallsBackToRawTruncationWhenTheLastSegmentAloneIsStillTooLong()
    {
        var truncated = HydraTui.TuiController.ShortCategory("Hydra.SomeExtremelyLongClassNameThatAloneExceedsTheBudget");

        Assert.That(truncated, Has.Length.EqualTo(24));
    }

    private static HydraStatusSnapshot Status(int processId, long uptime, long? relayAttempts = null) => new(
        DateTimeOffset.UtcNow, "0.0.0", processId, uptime, "config", "revision", "host", "profile",
        Hydra.Config.Mode.Master, false, relayAttempts != null,
        relayAttempts == null ? null : new RelayConnectionStatus(
            "en0", "Ethernet", "127.0.0.1", 50000, "relay", "127.0.0.1", 51600,
            DateTimeOffset.UtcNow, relayAttempts.Value, 0, 0, 0, 0),
        [], [], [], false, [], [], null);
}
