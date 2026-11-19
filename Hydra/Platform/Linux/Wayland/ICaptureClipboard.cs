namespace Hydra.Platform.Linux.Wayland;

// a clipboard the portal sets only while an input capture is on (the portal's, on an InputCapture session), which each
// visit lends its capture so the selection is the session's when the capture is let go
internal interface ICaptureClipboard
{
    // a capture is on: claim the selection while it may. Blocks on the portal; never throws
    void Claim();
}
