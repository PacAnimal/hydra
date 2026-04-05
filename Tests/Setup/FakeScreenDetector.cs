using Hydra.Relay;
using Hydra.Screen;

namespace Tests.Setup;

public class FakeScreenDetector : IScreenDetector
{
    public LocalScreenSnapshot Snapshot { get; set; } = new(
        [new ScreenRect("home:0", "home", 0, 0, 2560, 1440, IsLocal: true)],
        [new ScreenInfoEntry("home:0", 0, 0, 2560, 1440, 1.0m)]);

    public event Action<LocalScreenSnapshot>? ScreensChanged;

    public Task<LocalScreenSnapshot> Get(CancellationToken ct = default) => Task.FromResult(Snapshot);

    public void FireChange() => ScreensChanged?.Invoke(Snapshot);
}
