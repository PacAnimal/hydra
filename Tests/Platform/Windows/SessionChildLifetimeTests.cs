using System.Runtime.Versioning;
using Hydra.Platform.Windows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

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
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private StopRecorder _lifetime = null!;
    private string _stopEventName = null!;
    private SafeFileHandle _stopEvent = null!;
    private volatile string? _user;

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
    }

    [TearDown]
    public void TearDown() => _stopEvent.Dispose();

    [Test]
    public async Task AUserSignedInAtStartUp_IsStampedOffTheWatcher()
    {
        _user = "S-1-5-21-alice";
        var stampedOn = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = Lifetime(async _ =>
        {
            stampedOn.TrySetResult(Thread.CurrentThread.Name);
            await Task.CompletedTask;
        });

        await lifetime.StartAsync(CancellationToken.None);
        try
        {
            Assert.That(await stampedOn.Task.WaitAsync(Patience), Is.Not.EqualTo("session-stop-watcher"));
        }
        finally { await lifetime.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task AUserSigningInLater_IsStampedByThePoll()
    {
        var stamped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = Lifetime(async _ =>
        {
            stamped.TrySetResult();
            await Task.CompletedTask;
        });

        await lifetime.StartAsync(CancellationToken.None);
        try
        {
            Assert.That(stamped.Task.IsCompleted, Is.False, "stamped with nobody at the console");
            _user = "S-1-5-21-alice";
            await stamped.Task.WaitAsync(Patience);
        }
        finally { await lifetime.StopAsync(CancellationToken.None); }
    }

    // a stamp stuck behind the sidecar lock must not keep the watchdog's stop from being heard
    [Test]
    public async Task AStuckStamp_DoesNotDelayTheStopSignal()
    {
        _user = "S-1-5-21-alice";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = Lifetime(async cancel =>
        {
            started.TrySetResult();
            // blocks its thread, as a stamp waiting on the lock would if it ran on the watcher
            release.Task.Wait(cancel);
            await Task.CompletedTask;
        });

        await lifetime.StartAsync(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(Patience);
            Win32Session.SignalEvent(_stopEvent);
            await _lifetime.Stopped.WaitAsync(Patience);
        }
        finally
        {
            release.TrySetResult();
            await lifetime.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Stopping_CancelsAStampInFlight()
    {
        _user = "S-1-5-21-alice";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = Lifetime(async cancel =>
        {
            await using var _ = cancel.Register(() => abandoned.TrySetResult());
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancel);
        });

        await lifetime.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(Patience);
        await lifetime.StopAsync(CancellationToken.None);

        await abandoned.Task.WaitAsync(Patience);
    }

    [Test]
    public async Task AFailedConsoleUserLookup_DoesNotKillTheWatcher()
    {
        var lookups = 0;
        var stamped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new SessionChildLifetime(_lifetime, _stopEventName,
            () => Interlocked.Increment(ref lookups) == 1 ? throw new InvalidOperationException("no session") : "S-1-5-21-alice",
            async _ =>
            {
                stamped.TrySetResult();
                await Task.CompletedTask;
            }, NullLogger<SessionChildLifetime>.Instance);

        await lifetime.StartAsync(CancellationToken.None);
        try
        {
            await stamped.Task.WaitAsync(Patience);
            Win32Session.SignalEvent(_stopEvent);
            await _lifetime.Stopped.WaitAsync(Patience);
        }
        finally { await lifetime.StopAsync(CancellationToken.None); }
    }

    // the lookup runs four times a second, so a lasting failure warns once and not on every poll
    [Test]
    public async Task ALookupThatKeepsFailing_WarnsOnce()
    {
        var lookups = 0;
        var thirdLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new LevelLog();
        using var lifetime = new SessionChildLifetime(_lifetime, _stopEventName, () =>
        {
            if (Interlocked.Increment(ref lookups) == 3) thirdLookup.TrySetResult();
            throw new InvalidOperationException("no session");
        }, async _ => await Task.CompletedTask, log);

        await lifetime.StartAsync(CancellationToken.None);
        try
        {
            // the first two lookups have been logged by the time the third is made
            await thirdLookup.Task.WaitAsync(Patience);
        }
        finally { await lifetime.StopAsync(CancellationToken.None); }

        Assert.That(log.Levels()[..2], Is.EqualTo([LogLevel.Warning, LogLevel.Debug]));
    }

    // a good lookup ends the streak, so the next failure is a new one and warns again
    [Test]
    public async Task AFailureAfterAGoodLookup_WarnsAgain()
    {
        var lookups = 0;
        var fourthLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new LevelLog();
        using var lifetime = new SessionChildLifetime(_lifetime, _stopEventName, () =>
        {
            var lookup = Interlocked.Increment(ref lookups);
            if (lookup == 4) fourthLookup.TrySetResult();
            return lookup == 2 ? null : throw new InvalidOperationException("no session");
        }, async _ => await Task.CompletedTask, log);

        await lifetime.StartAsync(CancellationToken.None);
        try
        {
            // the first three lookups have been made by the time the fourth is made
            await fourthLookup.Task.WaitAsync(Patience);
        }
        finally { await lifetime.StopAsync(CancellationToken.None); }

        Assert.That(log.Levels()[..2], Is.EqualTo([LogLevel.Warning, LogLevel.Warning]));
    }

    private SessionChildLifetime Lifetime(Func<CancellationToken, Task> restamp) =>
        new(_lifetime, _stopEventName, () => _user, restamp, NullLogger<SessionChildLifetime>.Instance);

    private sealed class LevelLog : ILogger<SessionChildLifetime>
    {
        private readonly List<LogLevel> _levels = [];

        public LogLevel[] Levels()
        {
            lock (_levels) return [.. _levels];
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_levels) _levels.Add(logLevel);
        }
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
