namespace Hydra.FileTransfer;

public record FileSelectionResult(bool FileManagerFocused, List<string>? Paths);

public interface IFileSelectionDetector
{
    string FileManagerName { get; }
    bool IsFileTransferSupported { get; }

    // returns focused=false if the file manager is not the frontmost window.
    // returns focused=true with null paths if focused but nothing selected.
    // returns focused=true with paths if files are selected.
    FileSelectionResult GetSelectedPaths();
}

public sealed class NullFileSelectionDetector : IFileSelectionDetector
{
    public string FileManagerName => "file manager";
    public bool IsFileTransferSupported => false;

    public FileSelectionResult GetSelectedPaths() => new(true, null);
}
