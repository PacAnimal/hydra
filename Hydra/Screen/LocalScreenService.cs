using Cathedral.Extensions;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging;

namespace Hydra.Screen;

public interface ILocalScreenService
{
    Task<LocalScreenSnapshot> Get(CancellationToken ct = default);
    event Action<LocalScreenSnapshot>? ScreensChanged;
}

public record LocalScreenSnapshot(List<ScreenInfoEntry> Entries, Dictionary<string, ScreenRect> Map);

public class LocalScreenService(IPlatformOutput output, HydraConfig config, ILogger<LocalScreenService> log)
    : SimpleHostedService(log, loopTime: TimeSpan.FromSeconds(2)), ILocalScreenService
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<DetectedScreen> _detected = [];
    private LocalScreenSnapshot? _current;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<LocalScreenSnapshot>? ScreensChanged;

    public async Task<LocalScreenSnapshot> Get(CancellationToken ct = default)
    {
        await _ready.Task.WaitAsync(ct);
        return _current!;
    }

    protected override async Task Execute(CancellationToken cancel)
    {
        var detected = output.GetAllScreens();
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
            log.LogInformation("Local screens: {Count}", snapshot.Entries.Count);
            ScreensChanged?.Invoke(snapshot);
        }
    }

    private LocalScreenSnapshot Build(List<DetectedScreen> detected)
    {
        if (detected.Count == 0)
            return new LocalScreenSnapshot([], new Dictionary<string, ScreenRect>(StringComparer.OrdinalIgnoreCase));

        var minX = detected.Min(d => d.X);
        var minY = detected.Min(d => d.Y);

        var entries = new List<ScreenInfoEntry>();
        var map = new Dictionary<string, ScreenRect>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < detected.Count; i++)
        {
            var d = detected[i];
            var name = ScreenNaming.BuildScreenName(config.ResolvedName, i, detected.Count);
            var scale = ResolveScale(d);
            var nx = d.X - minX;
            var ny = d.Y - minY;
            entries.Add(new ScreenInfoEntry(name, nx, ny, d.Width, d.Height, scale));
            map[name] = new ScreenRect(name, config.ResolvedName, d.X, d.Y, d.Width, d.Height, IsLocal: true);
        }

        return new LocalScreenSnapshot(entries, map);
    }

    private decimal ResolveScale(DetectedScreen d)
    {
        foreach (var def in config.ScreenDefinitions)
        {
            if (Matches(d, def.Match))
                return def.Scale;
        }
        return 1.0m;
    }

    private static bool Matches(DetectedScreen d, string match) =>
        d.DisplayName?.EqualsIgnoreCase(match) is true
        || d.OutputName?.EqualsIgnoreCase(match) is true
        || d.PlatformId?.EqualsIgnoreCase(match) is true;
}
