using Hydra.Config;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Screen;

[TestFixture]
public class ScreenStartupTests
{
    [Test]
    public async Task StartAsync_NameNotInScreens_LogsErrorAndDoesNotCrash()
    {
        var profile = TransitionTestHelper.Profile("unknown-host", new HydraConfig
        {
            Mode = Mode.Master,
            Hosts =
            [
                new HostConfig { Name = "laptop", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "desktop" }] },
                new HostConfig { Name = "desktop", Neighbours = [new NeighbourConfig { Direction = Direction.Left, Name = "laptop" }] },
            ],
        });

        var logs = new ErrorCapture();
        var service = new InputRouter(
            new FakePlatform(), profile, new NullRelaySender(),
            new FakeScreenDetector(), NullLoggerFactory.Instance, logs.CreateLogger(), new NullScreenSaverSync(), new NullClipboardSync(), TransitionTestHelper.TempFiles);

        // must not throw
        await service.StartAsync(CancellationToken.None);

        Assert.That(logs.HasError, Is.True, "should log an error when hostname is not in screens");
    }

    // -- stubs --

    private sealed class ErrorCapture
    {
        public bool HasError { get; private set; }

        public ILogger<InputRouter> CreateLogger() => new ErrorLogger(this);

        private sealed class ErrorLogger(ErrorCapture capture) : ILogger<InputRouter>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error)
                    capture.HasError = true;
            }
        }
    }
}
