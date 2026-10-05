using Hydra.FileTransfer;

namespace Tests.Setup;

public sealed class FakeDropTargetResolver(string directory) : IDropTargetResolver
{
    public string GetPasteDirectory() => directory;
    public void MoveToDestination(string tempDir, string destDir) => FileUtils.MoveTo(tempDir, destDir);
}
