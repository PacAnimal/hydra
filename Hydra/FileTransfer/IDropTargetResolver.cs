namespace Hydra.FileTransfer;

public interface IDropTargetResolver
{
    // returns the destination directory for a paste — the active Finder/Explorer window, or the desktop.
    // returns null if no suitable destination is found (e.g. a non-file-manager app is focused).
    string? GetPasteDirectory();

    // moves extracted files from tempDir into destDir, handling conflicts per-platform.
    // on Windows this shows the shell's native conflict dialog; on other platforms files are auto-renamed.
    void MoveToDestination(string tempDir, string destDir);
}

public sealed class NullDropTargetResolver : IDropTargetResolver
{
    public string? GetPasteDirectory() => null;
    public void MoveToDestination(string tempDir, string destDir) => FileUtils.MoveTo(tempDir, destDir);
}
