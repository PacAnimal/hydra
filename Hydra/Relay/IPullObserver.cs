namespace Hydra.Relay;

// A clipboard told of the pulls the master sends, for one that serves the clipboard on request after the pull went out
// (Wayland: the portal's selection, which an app pasting asks for) and should serve that pull's answer when it is coming
internal interface IPullObserver
{
    // a pull went out; settled completes once its answer is written or found unchanged, or the pull is given up
    // (forgotten, superseded, replaced, its stream ended)
    void PullSent(Task settled);
}
