using Hydra.Screen;

namespace Hydra.FileTransfer;

public interface IFileDragSource
{
    // returns file/directory paths if the OS currently has an active file drag, null otherwise.
    // called on the event tap thread when left button is held during edge crossing.
    List<string>? GetDraggedPaths();

    // called after each layout rebuild with the updated active transition edge ranges.
    // implementations that use per-edge detection regions (e.g. Windows OLE strips) should recreate them here.
    void UpdateActiveEdges(List<ActiveEdgeRange> ranges) { }
}

public sealed class NullFileDragSource : IFileDragSource
{
    public List<string>? GetDraggedPaths() => null;
}
