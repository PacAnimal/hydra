using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Screen;

[TestFixture]
public class ScreenStartupTests
{
    [Test]
    public async Task StartAsync_NameNotInScreens_LogsErrorAndDoesNotCrash()
    {
        var config = new HydraConfig
        {
            Mode = Mode.Master,
            Name = "unknown-host",
            Hosts =
            [
                new HostConfig { Name = "laptop", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "desktop" }] },
                new HostConfig { Name = "desktop", Neighbours = [new NeighbourConfig { Direction = Direction.Left, Name = "laptop" }] },
            ],
        };

        var logs = new ErrorCapture();
        var service = new ScreenTransitionService(
            new FakePlatform(), config, new NullRelaySender(),
            NullLoggerFactory.Instance, logs.CreateLogger());

        // must not throw
        await service.StartAsync(CancellationToken.None);

        Assert.That(logs.HasError, Is.True, "should log an error when hostname is not in screens");
    }

    // -- stubs --

    private sealed class ErrorCapture
    {
        public bool HasError { get; private set; }

        public ILogger<ScreenTransitionService> CreateLogger() => new ErrorLogger(this);

        private sealed class ErrorLogger(ErrorCapture capture) : ILogger<ScreenTransitionService>
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

    private sealed class FakePlatform : IPlatformInput
    {
        public bool IsOnVirtualScreen { get; set; }
        public ScreenRect GetPrimaryScreenBounds() => new("laptop", "laptop", 0, 0, 2560, 1440, IsLocal: true);
        public List<DetectedScreen> GetAllScreens() => [new DetectedScreen(0, 0, 2560, 1440, null, null, null)];
        public bool IsAccessibilityTrusted() => true;
        public void StartEventTap(Action<double, double> onMouseMove, Action<KeyEvent> onKeyEvent, Action<MouseButtonEvent> onMouseButton, Action<MouseScrollEvent> onMouseScroll) { }
        public void StopEventTap() { }
        public void WarpCursor(int x, int y) { }
        public void HideCursor() { }
        public void ShowCursor() { }
        public void Dispose() { }
    }
}
