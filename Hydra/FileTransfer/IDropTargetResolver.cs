namespace Hydra.FileTransfer;

public interface IDropTargetResolver
{
    // returns the directory path under the cursor if the cursor is over an open folder view, null otherwise.
    // null causes FileTransferService to fall back to Desktop.
    string? GetDirectoryUnderCursor();

    // moves extracted files from tempDir into destDir, handling conflicts per-platform.
    // on Windows this shows the shell's native conflict dialog; on other platforms files are auto-renamed.
    void MoveToDestination(string tempDir, string destDir);
}

public sealed class NullDropTargetResolver : IDropTargetResolver
{
    public string? GetDirectoryUnderCursor() => null;
    public void MoveToDestination(string tempDir, string destDir) => FileUtils.MoveTo(tempDir, destDir);
}
