using Hydra.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform.Windows;

[TestFixture]
public class WindowsInputHandlerWarpStateTests
{
    // SetWarpState/GetLastWarp/RecentreToTarget/ResyncLastWarp are written from two real threads in
    // production — the dedicated Win32 hook thread (~900/s) and the router's consumer thread
    // (rarely, at a screen entry/exit) — with no synchronisation other than the internal _warpLock.
    // Every write here uses X == Y, so any read observing X != Y is a torn pair: proof the lock let
    // two writes interleave instead of serialising them. This runs in the default (mac/Linux) lane,
    // same as WinKeyResolverTests — the class under test does no P/Invoke until a method that
    // actually calls one is invoked, and none of these four methods do.
    [Test]
    public void ConcurrentWarpStateAccess_NeverObservesATornPair()
    {
        var handler = new WindowsInputHandler(NullLogger<WindowsInputHandler>.Instance, TransitionTestHelper.Profile("home"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var token = cts.Token;
        var failure = (string?)null;

        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!token.IsCancellationRequested)
                handler.SetWarpState(++i, i);
        });

        var reader = Task.Run(() =>
        {
            var j = 0;
            while (!token.IsCancellationRequested)
            {
                Check(handler.GetLastWarp(), nameof(handler.GetLastWarp));
                Check(handler.RecentreToTarget(), nameof(handler.RecentreToTarget));
                handler.ResyncLastWarp(++j, j);
            }

            void Check((int X, int Y) pair, string source)
            {
                if (pair.X != pair.Y)
                    failure ??= $"{source} returned a torn pair: ({pair.X}, {pair.Y})";
            }
        });

        Task.WaitAll(writer, reader);
        Assert.That(failure, Is.Null, failure);
    }
}
