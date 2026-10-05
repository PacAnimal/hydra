namespace Hydra.FileTransfer;

public record FileSelectionResult(bool FileManagerFocused, List<string>? Paths)
{
    // the file manager could not be asked, as opposed to answering that it is not focused
    public bool Failed { get; private init; }

    public static readonly FileSelectionResult Failure = new(false, null) { Failed = true };

    public const string FailedMessage = "Copy failed";
    public const string InProgressMessage = "Copy in progress";
    public const string UnavailableMessage = "Copy not available";
}

public interface IFileSelectionDetector
{
    string FileManagerName { get; }
    bool IsFileTransferSupported { get; }

    // returns focused=false if the file manager is not the frontmost window.
    // returns focused=true with null paths if focused but nothing selected.
    // returns focused=true with paths if files are selected.
    // returns Failure if the file manager could not be asked at all.
    // slow: Finder can block for up to OsaScript.FinderTimeout behind its consent prompt, so keep it off input paths.
    // cancelling abandons the query and throws OperationCanceledException.
    FileSelectionResult GetSelectedPaths(CancellationToken cancel);
}

public sealed class NullFileSelectionDetector : IFileSelectionDetector
{
    public string FileManagerName => "file manager";
    public bool IsFileTransferSupported => false;

    public FileSelectionResult GetSelectedPaths(CancellationToken cancel) => new(true, null);
}
