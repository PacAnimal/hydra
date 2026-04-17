namespace Hydra.FileTransfer;

public interface IFileSelectionDetector
{
    // returns paths of files/dirs selected in the frontmost file manager, null if none
    List<string>? GetSelectedPaths();
}

public sealed class NullFileSelectionDetector : IFileSelectionDetector
{
    public List<string>? GetSelectedPaths() => null;
}
