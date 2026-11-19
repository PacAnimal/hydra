namespace Hydra.Platform;

// A master's platform that sees the pointer at home only as it hits a pointer barrier (Wayland). Each hit reaches the
// router as an absolute move tagged with the hit's serial, and one that starts no visit is answered StayedHome with that
// serial: an answer arriving late then releases only the hit it came from, never a newer one
internal interface IBarrierHits
{
    // where the hits go, in place of the event tap's absolute moves; set before the tap starts
    void ReportHitsTo(Action<double, double, long> onHit);

    // the hit with that serial started no visit
    void StayedHome(long hit);
}
