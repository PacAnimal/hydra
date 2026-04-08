using System.Text.Json;
using System.Text.Json.Serialization;
using Cathedral.Extensions;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra.Screen;

public interface IScreenDetector
{
    Task<LocalScreenSnapshot> Get(CancellationToken ct = default);
    event Func<LocalScreenSnapshot, Task>? ScreensChanged;
}

public record LocalScreenSnapshot(List<ScreenRect> Screens, List<ScreenInfoEntry> Entries);

public abstract class ScreenDetector : SimpleHostedService, IScreenDetector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HydraConfig _config;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<DetectedScreen> _detected = [];
    private LocalScreenSnapshot? _current;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Func<LocalScreenSnapshot, Task>? ScreensChanged;

    // ReSharper disable once ConvertToPrimaryConstructor
#pragma warning disable IDE0290
    protected ScreenDetector(HydraConfig config, ILogger log) : base(log, loopTime: TimeSpan.FromSeconds(2))
    {
        _config = config;
        _log = log;
    }
#pragma warning restore IDE0290

    protected abstract List<DetectedScreen> Detect();

    public async Task<LocalScreenSnapshot> Get(CancellationToken ct = default)
    {
        await _ready.Task.WaitAsync(ct);
        return _current!;
    }

    protected override async Task Execute(CancellationToken cancel)
    {
        var detected = Detect();
        LocalScreenSnapshot? snapshot = null;

        using (await _lock.WaitForDisposable(cancel))
        {
            if (_current == null || ScreenRect.ScreenListChanged(detected, _detected))
            {
                snapshot = Build(detected);
                _detected = detected;
                _current = snapshot;
            }
        }

        _ready.TrySetResult();

        if (snapshot != null)
        {
            _log.LogInformation("Local screens: {Count}", snapshot.Screens.Count);
            for (var i = 0; i < snapshot.Screens.Count; i++)
                if (snapshot.Screens[i].Identity != null)
                    _log.LogInformation("  Screen {I}: {Json}", i, JsonSerializer.Serialize(snapshot.Screens[i].Identity, JsonOptions));
            if (ScreensChanged != null) await ScreensChanged(snapshot);
        }
    }

    private LocalScreenSnapshot Build(List<DetectedScreen> detected)
    {
        if (detected.Count == 0)
            return new LocalScreenSnapshot([], []);

        var minX = detected.Min(d => d.X);
        var minY = detected.Min(d => d.Y);

        var screens = new List<ScreenRect>();
        var entries = new List<ScreenInfoEntry>();

        for (var i = 0; i < detected.Count; i++)
        {
            var d = detected[i];
            var name = ScreenNaming.BuildScreenName(_config.ResolvedName, i, detected.Count);
            var scale = ResolveScale(d);
            var identity = new ScreenIdentity
            {
                ScreenName = name,
                Output = d.OutputName,
                DisplayName = d.DisplayName,
                PlatformId = d.PlatformId,
            };
            screens.Add(new ScreenRect(name, _config.ResolvedName, d.X, d.Y, d.Width, d.Height, IsLocal: true, Identity: identity));
            entries.Add(new ScreenInfoEntry(name, d.X - minX, d.Y - minY, d.Width, d.Height, scale));
        }

        return new LocalScreenSnapshot(screens, entries);
    }

    private decimal ResolveScale(DetectedScreen d)
    {
        foreach (var def in _config.ScreenDefinitions)
        {
            if (Matches(d, def))
                return def.MouseScale ?? _config.MouseScale ?? 1.0m;
        }
        return _config.MouseScale ?? 1.0m;
    }

    private static bool Matches(DetectedScreen d, ScreenDefinition def) =>
        (def.DisplayName == null || d.DisplayName?.EqualsIgnoreCase(def.DisplayName) is true)
        && (def.OutputName == null || d.OutputName?.EqualsIgnoreCase(def.OutputName) is true)
        && (def.PlatformId == null || d.PlatformId?.EqualsIgnoreCase(def.PlatformId) is true);
}

// no-op screen detector for console/headless mode — no local screens to detect
public sealed class NullScreenDetector : IScreenDetector, IHostedService
{
    private static readonly LocalScreenSnapshot Empty = new([], []);
#pragma warning disable CS0067  // never fired — headless mode has no screen changes
    public event Func<LocalScreenSnapshot, Task>? ScreensChanged;
#pragma warning restore CS0067
    public Task<LocalScreenSnapshot> Get(CancellationToken ct = default) => Task.FromResult(Empty);
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
