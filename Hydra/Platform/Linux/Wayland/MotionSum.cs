namespace Hydra.Platform.Linux.Wayland;

// one frame's summed motion
internal sealed record FrameMotion(double Dx, double Dy);

// The EIS receiver's motion summed per frame, kept only during a visit. Not thread-safe: the handler holds its lock.
internal sealed class MotionSum
{
    private double _dx, _dy;

    // motion while not visiting (a barrier hit the router has not entered yet) is dropped
    internal void Add(double dx, double dy, bool visiting)
    {
        if (!visiting) return;
        _dx += dx;
        _dy += dy;
    }

    // the frame's motion, emptied; null when there is none to hand on
    internal FrameMotion? Take(bool visiting)
    {
        var motion = new FrameMotion(_dx, _dy);
        Reset();
        return visiting && (motion.Dx != 0 || motion.Dy != 0) ? motion : null;
    }

    internal void Reset() => _dx = _dy = 0;
}
