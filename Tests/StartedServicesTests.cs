using Microsoft.Extensions.Hosting;
using Tests.Setup;

namespace Tests;

[TestFixture]
public class StartedServicesTests
{
    [Test]
    public async Task StopAll_StopsEveryServiceEvenWhenOneThrows()
    {
        var services = new StartedServices();
        var first = await services.Start(new RecordingService());
        await services.Start(new RecordingService { Fails = true });

        Assert.ThrowsAsync<InvalidOperationException>(services.StopAll, "a single failure should surface as itself");
        Assert.That(first.Stopped, Is.True, "the older service was left running behind the failing one");
    }

    [Test]
    public async Task StopAll_AggregatesTwoOrMoreFailures()
    {
        var services = new StartedServices();
        await services.Start(new RecordingService { Fails = true });
        await services.Start(new RecordingService { Fails = true });

        var thrown = Assert.ThrowsAsync<AggregateException>(services.StopAll);

        Assert.That(thrown.InnerExceptions, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task StopAll_DisposesWhatItOwnsAfterTheServicesStop()
    {
        var services = new StartedServices();
        var service = await services.Start(new RecordingService { Fails = true });
        var owned = services.Own(new RecordingDisposable(service));

        Assert.ThrowsAsync<InvalidOperationException>(services.StopAll);

        Assert.That(owned.DisposedAfterStop, Is.True, "the owned resource was not disposed, or was disposed before the service stopped");
    }

    [Test]
    public async Task StopAll_FailsLoudlyOnAServiceThatNeverStops_AndStillStopsTheRest()
    {
        var services = new StartedServices(TimeSpan.FromMilliseconds(100));
        var first = await services.Start(new RecordingService());
        await services.Start(new NeverStoppingService());

        var thrown = Assert.ThrowsAsync<TimeoutException>(services.StopAll);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown.Message, Does.Contain(nameof(NeverStoppingService)));
            Assert.That(first.Stopped, Is.True, "the older service was left running behind the hung one");
        }
    }

    // BackgroundService gives up on the token it is handed and returns as though stopped
    [Test]
    public async Task StopAll_FailsLoudlyOnABackgroundServiceIgnoringItsStoppingToken()
    {
        var services = new StartedServices(TimeSpan.FromMilliseconds(100));
        var hung = await services.Start(new TokenIgnoringService());
        try
        {
            // stopped before ExecuteAsync runs, BackgroundService cancels it unstarted and stops at once
            await hung.Executing.WaitAsync(TimeSpan.FromSeconds(30));
            var thrown = Assert.ThrowsAsync<TimeoutException>(services.StopAll);

            Assert.That(thrown.Message, Does.Contain(nameof(TokenIgnoringService)));
        }
        finally
        {
            hung.Release();
        }
    }

    [Test]
    public async Task Stop_StopsThatServiceOnce_AndStopAllLeavesItAlone()
    {
        var services = new StartedServices();
        var stopped = await services.Start(new RecordingService());
        var running = await services.Start(new RecordingService());

        await services.Stop(stopped);
        await services.StopAll();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stopped.StopCount, Is.EqualTo(1));
            Assert.That(running.StopCount, Is.EqualTo(1));
        }
    }

    private sealed class NeverStoppingService : IHostedService
    {
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartAsync(CancellationToken cancellationToken) => await Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => _never.Task;
    }

    private sealed class TokenIgnoringService : BackgroundService
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _executing = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Executing => _executing.Task;

        public void Release() => _released.TrySetResult();

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _executing.TrySetResult();
            return _released.Task;
        }
    }

    private sealed class RecordingDisposable(RecordingService service) : IAsyncDisposable
    {
        public bool? DisposedAfterStop { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await ValueTask.CompletedTask;
            DisposedAfterStop = service.Stopped;
        }
    }

    private sealed class RecordingService : IHostedService
    {
        public bool Fails { get; init; }
        public int StopCount { get; private set; }
        public bool Stopped => StopCount > 0;

        public async Task StartAsync(CancellationToken cancellationToken) => await Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            StopCount++;
            if (Fails) throw new InvalidOperationException("stop failed");
        }
    }
}
