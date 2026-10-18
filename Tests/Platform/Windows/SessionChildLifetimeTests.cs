using System.Runtime.Versioning;
using Hydra.Platform.Windows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Tests.Setup;

namespace Tests.Platform.Windows;

/// <summary>
/// The session child's watcher stamps the sidecar at start-up and whenever a console user appears later, never
/// on its own thread, and abandons a stamp when it stops. Each test owns a uniquely named stop event, so a real
/// Hydra service on the box is never signalled.
/// </summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public class SessionChildLifetimeTests
{
    private StopRecorder _lifetime = null!;
    private string _stopEventName = null!;
    private SafeFileHandle _stopEvent = null!;
    private volatile string? _user;
    private StartedServices _services = null!;
    private readonly List<SessionChildLifetime> _made = [];

    [OneTimeSetUp]
    public void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows-only: named events");
    }

    [SetUp]
    public void SetUp()
    {
        _user = null;
        _lifetime = new StopRecorder();
        _stopEventName = $"HydraTestSessionStop-{Guid.NewGuid():N}";
        _stopEvent = Win32Session.CreateGlobalEvent(_stopEventName, manualReset: true);
        _services = new StartedServices();
    }

    // disposed only once stopped: a lifetime disposed while it runs cannot be stopped
    [TearDown]
    public async Task TearDown()
    {
        try
        {
            await _services.StopAll();
        }
        finally
        {
            foreach (var lifetime in _made) lifetime.Dispose();
            _made.Clear();
            _stopEvent.Dispose();
        }
    }

    [Test]
    public async Task AUserSignedInAtStartUp_IsStampedOffTheWatcher()
    {
        _user = "S-1-5-21-alice";
        var stampedOn = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _services.Start(Lifetime(async _ =>
        {
            stampedOn.TrySetResult(Thread.CurrentThread.Name);
            await Task.CompletedTask;
        }));

        Assert.That(await stampedOn.Task.WaitAsync(Bound), Is.Not.EqualTo("session-stop-watcher"));
    }

    [Test]
    public async Task AUserSigningInLater_IsStampedByThePoll()
    {
        var lookups = 0;
        var secondLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stamped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _services.Start(Lifetime(() =>
        {
            var user = _user;
            if (Interlocked.Increment(ref lookups) == 2) secondLookup.TrySetResult();
            return user;
        }, async _ =>
        {
            stamped.TrySetResult();
            await Task.CompletedTask;
        }));

        // no poll looks the user up while a stamp is in flight, so by the second lookup any stamp the first started has run
        await secondLookup.Task.WaitAsync(Bound);
        Assert.That(stamped.Task.IsCompleted, Is.False, "stamped with nobody at the console");
        _user = "S-1-5-21-alice";
        await stamped.Task.WaitAsync(Bound);
    }

    // a stamp stuck behind the sidecar lock must not keep the watchdog's stop from being heard
    [Test]
    public async Task AStuckStamp_DoesNotDelayTheStopSignal()
    {
        _user = "S-1-5-21-alice";
        var sidecarLock = new Gate(held: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _services.Start(Lifetime(async cancel =>
        {
            started.TrySetResult();
            // blocks its thread, as a stamp waiting on the lock would if it ran on the watcher; the stop cancels it
            sidecarLock.Passed(cancel).GetAwaiter().GetResult();
            await Task.CompletedTask;
        }));

        await started.Task.WaitAsync(Bound);
        Win32Session.SignalEvent(_stopEvent);
        await _lifetime.Stopped.WaitAsync(Bound);
    }

    [Test]
    public async Task Stopping_CancelsAStampInFlight()
    {
        _user = "S-1-5-21-alice";
        var sidecarLock = new Gate(held: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = await _services.Start(Lifetime(async cancel =>
        {
            await using var _ = cancel.Register(() => abandoned.TrySetResult());
            started.TrySetResult();
            await sidecarLock.Passed(cancel);
        }));

        await started.Task.WaitAsync(Bound);
        await _services.Stop(lifetime);

        await abandoned.Task.WaitAsync(Bound);
    }

    [Test]
    public async Task AFailedConsoleUserLookup_DoesNotKillTheWatcher()
    {
        var lookups = 0;
        var stamped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _services.Start(Lifetime(
            () => Interlocked.Increment(ref lookups) == 1 ? throw new InvalidOperationException("no session") : "S-1-5-21-alice",
            async _ =>
            {
                stamped.TrySetResult();
                await Task.CompletedTask;
            }));

        await stamped.Task.WaitAsync(Bound);
        Win32Session.SignalEvent(_stopEvent);
        await _lifetime.Stopped.WaitAsync(Bound);
    }

    // the lookup runs four times a second, so a lasting failure warns once and not on every poll
    [Test]
    public async Task ALookupThatKeepsFailing_WarnsOnce()
    {
        var lookups = 0;
        var thirdLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new CapturingLog<SessionChildLifetime>();
        await _services.Start(Lifetime(() =>
        {
            if (Interlocked.Increment(ref lookups) == 3) thirdLookup.TrySetResult();
            throw new InvalidOperationException("no session");
        }, async _ => await Task.CompletedTask, log));

        // the first two lookups have been logged by the time the third is made
        await thirdLookup.Task.WaitAsync(Bound);

        Assert.That(log.Entries.Select(e => e.Level).Take(2), Is.EqualTo([LogLevel.Warning, LogLevel.Debug]));
    }

    // a good lookup ends the streak, so the next failure is a new one and warns again
    [Test]
    public async Task AFailureAfterAGoodLookup_WarnsAgain()
    {
        var lookups = 0;
        var fourthLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new CapturingLog<SessionChildLifetime>();
        await _services.Start(Lifetime(() =>
        {
            var lookup = Interlocked.Increment(ref lookups);
            if (lookup == 4) fourthLookup.TrySetResult();
            return lookup == 2 ? null : throw new InvalidOperationException("no session");
        }, async _ => await Task.CompletedTask, log));

        // the first three lookups have been made by the time the fourth is made
        await fourthLookup.Task.WaitAsync(Bound);

        Assert.That(log.Entries.Select(e => e.Level).Take(2), Is.EqualTo([LogLevel.Warning, LogLevel.Warning]));
    }

    private SessionChildLifetime Lifetime(Func<CancellationToken, Task> restamp) => Lifetime(() => _user, restamp);

    private SessionChildLifetime Lifetime(Func<string?> consoleUser, Func<CancellationToken, Task> restamp, ILogger<SessionChildLifetime>? log = null)
    {
        var lifetime = new SessionChildLifetime(_lifetime, _stopEventName, consoleUser, restamp, log ?? NullLogger<SessionChildLifetime>.Instance);
        _made.Add(lifetime);
        return lifetime;
    }

    private sealed class StopRecorder : IHostApplicationLifetime
    {
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Stopped => _stopped.Task;
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopped.TrySetResult();
    }
}
