using Cathedral.Utils;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

public interface ICursor
{
    ValueTask HideCursor();
    ValueTask ShowCursor();
    void WarpCursor(int x, int y) { }
}

public interface ICursorHider
{
    void Hide();
    void Show();
    void UpdateWarpPoint(int x, int y) { }
}

public sealed class CursorHiderService(ICursor cursor, ILogger<CursorHiderService> log)
    : SimpleHostedService(log, TimeSpan.FromSeconds(1)), ICursorHider
{
    private volatile bool _hidden;
    private volatile bool _pendingShow;
    private volatile int _warpX;
    private volatile int _warpY;

    public void Hide() { _pendingShow = false; _hidden = true; Trigger(); }
    public void Show() { _hidden = false; _pendingShow = true; Trigger(); }
    public void UpdateWarpPoint(int x, int y) { _warpX = x; _warpY = y; }

    protected override async Task Execute(CancellationToken cancel)
    {
        if (_hidden)
        {
            cursor.WarpCursor(_warpX, _warpY);
            await cursor.HideCursor();
        }
        else if (_pendingShow)
        {
            _pendingShow = false;
            await cursor.ShowCursor();
        }
    }

    protected override async Task OnShutdown(CancellationToken cancel) => await cursor.ShowCursor();
}
