using Hydra.Screen;

namespace Hydra.Platform.Linux.Wayland;

// a pointer barrier along one local screen's edge, in global logical pixels as the InputCapture portal takes it: a
// vertical one lies on the left of its pixel column, a horizontal one on the top of its row, both ends inclusive
internal sealed record BarrierLine(ScreenRect Screen, Direction Edge, int X1, int Y1, int X2, int Y2)
{
    public override string ToString() => $"{Edge} of {Screen.Name} ({X1},{Y1})-({X2},{Y2})";
}

// The geometry between the router's crossable spans and the portal's barriers. KDE (xdg-desktop-portal-kde) refuses a
// barrier that does not fill its screen's edge, and both KDE and mutter one on an edge another monitor touches;
// xdg-desktop-portal-gnome then fails the whole set. So a barrier is always a whole free edge.
internal static class WaylandBarriers
{
    // the zones' bounds, every local screen the compositor has
    internal static IEnumerable<ScreenBounds> Bounds(PortalZoneSet zones) =>
        zones.Zones.Select(z => new ScreenBounds(z.X, z.Y, (int)z.Width, (int)z.Height));

    // ids for a new set of count barriers, after the last id handed out, never 0 and never one of the previous set's: an
    // Activated already queued for an old barrier then finds none rather than the wrong line
    internal static uint[] Ids(ref uint last, int count)
    {
        var ids = new uint[count];
        for (var i = 0; i < count; i++)
        {
            last = last == uint.MaxValue ? 1 : last + 1;
            ids[i] = last;
        }
        return ids;
    }

    // the wait before retry attempt (0-based): doubling from first, capped at max
    internal static TimeSpan Backoff(int attempt, TimeSpan first, TimeSpan max) =>
        TimeSpan.FromTicks(Math.Min(max.Ticks, first.Ticks << Math.Clamp(attempt, 0, 20)));

    // one barrier along the whole of every edge with a crossable span, as KDE requires (it refuses one that does not fill
    // its edge); an edge touching another local screen anywhere along it gets none, as both KDE and mutter refuse it.
    // Where along the edge a hit crosses stays the router's call. screens are every local screen known
    internal static IReadOnlyList<BarrierLine> Lines(IReadOnlyList<CrossableEdge> edges, IEnumerable<ScreenBounds> screens)
    {
        var local = edges.Select(e => e.Screen.Bounds).Concat(screens).Distinct().ToList();
        var lines = new List<BarrierLine>();
        foreach (var (screen, dir) in edges.Select(e => (e.Screen, e.Span.Direction)).Distinct())
        {
            if (screen.Width < 2 || screen.Height < 2) continue;
            var line = dir switch
            {
                Direction.Left => new BarrierLine(screen, dir, screen.X, screen.Y, screen.X, screen.Y + screen.Height - 1),
                Direction.Right => new BarrierLine(screen, dir, screen.X + screen.Width, screen.Y, screen.X + screen.Width, screen.Y + screen.Height - 1),
                Direction.Up => new BarrierLine(screen, dir, screen.X, screen.Y, screen.X + screen.Width - 1, screen.Y),
                _ => new BarrierLine(screen, dir, screen.X, screen.Y + screen.Height, screen.X + screen.Width - 1, screen.Y + screen.Height),
            };
            if (!local.Any(other => other != screen.Bounds && Touches(other, line))) lines.Add(line);
        }
        return lines;
    }

    // whether another screen lies on the line or against it: the line on its edge or inside it, over a shared stretch
    private static bool Touches(ScreenBounds other, BarrierLine line) => line.X1 == line.X2
        ? other.X <= line.X1 && line.X1 <= other.X + other.Width && line.Y1 < other.Y + other.Height && other.Y <= line.Y2
        : other.Y <= line.Y1 && line.Y1 <= other.Y + other.Height && line.X1 < other.X + other.Width && other.X <= line.X2;

    // where the router sees a hit: on the screen's outermost pixel of the barrier's edge. A top or bottom hit stays off
    // the corner columns, which the router reads as the side edges' (ScreenLayout.ExitEdge)
    internal static ScreenPoint HitPoint(BarrierLine barrier, double x, double y)
    {
        var screen = barrier.Screen;
        return barrier.Edge switch
        {
            Direction.Left => new(screen.X, Clamp(y, barrier.Y1, barrier.Y2)),
            Direction.Right => new(screen.X + screen.Width - 1, Clamp(y, barrier.Y1, barrier.Y2)),
            Direction.Up => new(Clamp(x, barrier.X1 + 1, barrier.X2 - 1), screen.Y),
            _ => new(Clamp(x, barrier.X1 + 1, barrier.X2 - 1), screen.Y + screen.Height - 1),
        };
    }

    // where a refused hit puts the pointer back: one pixel further in than the hit, so it does not trip the barrier again
    internal static ScreenPoint ReleasePoint(BarrierLine barrier, ScreenPoint hit)
    {
        var screen = barrier.Screen;
        return barrier.Edge switch
        {
            Direction.Left => hit with { X = screen.X + 1 },
            Direction.Right => hit with { X = screen.X + screen.Width - 2 },
            Direction.Up => hit with { Y = screen.Y + 1 },
            _ => hit with { Y = screen.Y + screen.Height - 2 },
        };
    }

    // the barrier closest to (x, y), for a hit the compositor could not name; null when there is none
    internal static BarrierLine? Nearest(IEnumerable<BarrierLine> barriers, double x, double y)
    {
        BarrierLine? nearest = null;
        var best = double.MaxValue;
        foreach (var barrier in barriers)
        {
            var dx = x - Math.Clamp(x, barrier.X1, barrier.X2);
            var dy = y - Math.Clamp(y, barrier.Y1, barrier.Y2);
            var distance = dx * dx + dy * dy;
            if (distance >= best) continue;
            best = distance;
            nearest = barrier;
        }
        return nearest;
    }

    private static int Clamp(double value, int min, int max) =>
        double.IsFinite(value) ? (int)Math.Clamp(Math.Floor(value), min, Math.Max(min, max)) : min;
}
