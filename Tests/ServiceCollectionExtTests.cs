using Hydra;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Tests;

[TestFixture]
public class ServiceCollectionExtTests
{
    [Test]
    public async Task AddHostedSingleton_RunsTheInstanceItResolves()
    {
        await using var provider = new ServiceCollection().AddHostedSingleton<FakeHostedService>().BuildServiceProvider();

        Assert.That(provider.GetServices<IHostedService>().Single(), Is.SameAs(provider.GetRequiredService<FakeHostedService>()));
    }

    [Test]
    public async Task AddHostedSingleton_FromAFactory_RunsTheInstanceItResolves()
    {
        var calls = 0;
        await using var provider = new ServiceCollection()
            .AddHostedSingleton(_ => { calls++; return new FakeHostedService(); })
            .BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>().Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hosted, Is.SameAs(provider.GetRequiredService<FakeHostedService>()));
            Assert.That(calls, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task AddHostedSingleton_FromAnInstance_RunsThatInstance()
    {
        var instance = new FakeHostedService();
        await using var provider = new ServiceCollection().AddHostedSingleton(instance).BuildServiceProvider();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.GetServices<IHostedService>().Single(), Is.SameAs(instance));
            Assert.That(provider.GetRequiredService<FakeHostedService>(), Is.SameAs(instance));
        }
    }

    [Test]
    public async Task AddCoalescedOutput_WrapsTheHandlerAndServesItAsTheCursor()
    {
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddCoalescedOutput<FakeOutputHandler>(handler => handler.Initialized++)
            .BuildServiceProvider();

        var output = provider.GetRequiredService<IPlatformOutput>();
        var handler = provider.GetRequiredService<FakeOutputHandler>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(output, Is.TypeOf<CoalescingOutputWrapper>());
            Assert.That(provider.GetRequiredService<ICursor>(), Is.SameAs(handler));
            Assert.That(handler.Initialized, Is.EqualTo(1));
        }
    }

    private sealed class FakeHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeOutputHandler : IPlatformOutput, ICursor
    {
        public int Initialized { get; set; }
        public void MoveMouse(int x, int y) { }
        public void MoveMouseRelative(int dx, int dy) { }
        public void InjectKey(KeyEventMessage msg) { }
        public void InjectMouseButton(MouseButtonMessage msg) { }
        public void InjectMouseScroll(MouseScrollMessage msg) { }
        public ValueTask HideCursor() => ValueTask.CompletedTask;
        public ValueTask ShowCursor() => ValueTask.CompletedTask;
        public void Dispose() { }
    }
}
