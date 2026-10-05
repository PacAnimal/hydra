using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;

namespace Tests.Setup;

/// <summary>
/// The services a test started, stopped from its fixture's <c>[TearDown]</c>. A stop at the end of the test
/// body is skipped by the first failing assertion, and a service left running keeps working on the fixture's
/// shared directory while the next test sets it up.
/// </summary>
internal sealed class StartedServices
{
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(30);

    private readonly List<IHostedService> _running = [];
    private readonly List<IAsyncDisposable> _owned = [];
    private readonly TimeSpan _stopTimeout;

    internal StartedServices() : this(DefaultStopTimeout) { }

    // a stop that takes longer than this fails the teardown rather than hanging the run
    internal StartedServices(TimeSpan stopTimeout) => _stopTimeout = stopTimeout;

    internal async Task<T> Start<T>(T service) where T : IHostedService
    {
        await service.StartAsync(CancellationToken.None);
        _running.Add(service);
        return service;
    }

    // the router, with its platform disposed after it stops
    internal async Task<TestServiceBundle> Start(TestServiceBundle bundle)
    {
        Own(bundle.Platform);
        await Start(bundle.Service);
        return bundle;
    }

    // stops one service now, for a test about stopping; the teardown then leaves it alone
    internal async Task Stop(IHostedService service)
    {
        _running.Remove(service);
        await StopBounded(service);
    }

    // disposed once every service has stopped, since a service may still be using it until then
    internal T Own<T>(T resource) where T : IAsyncDisposable
    {
        _owned.Add(resource);
        return resource;
    }

    // newest first, as a host stops them, and every one of them even when an earlier stop or disposal throws
    internal async Task StopAll()
    {
        List<Exception> failures = [];
        for (var i = _running.Count - 1; i >= 0; i--)
        {
            var service = _running[i];
            _running.RemoveAt(i);
            try
            {
                await StopBounded(service);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            var resource = _owned[i];
            _owned.RemoveAt(i);
            try
            {
                await resource.DisposeAsync();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }
        // a lone failure keeps its own type and stack in the teardown output
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    // one deadline for both, and a BackgroundService handed a cancelled token returns as though it stopped
    private async Task StopBounded(IHostedService service)
    {
        using var giveUp = new CancellationTokenSource(_stopTimeout);
        try
        {
            await service.StopAsync(giveUp.Token).WaitAsync(giveUp.Token);
        }
        catch (OperationCanceledException ex) when (giveUp.IsCancellationRequested)
        {
            throw StopTimedOut(service, ex);
        }
        if (giveUp.IsCancellationRequested) throw StopTimedOut(service, null);
    }

    private TimeoutException StopTimedOut(IHostedService service, Exception? inner) =>
        new($"{service.GetType().Name} did not stop within {_stopTimeout}", inner);
}
