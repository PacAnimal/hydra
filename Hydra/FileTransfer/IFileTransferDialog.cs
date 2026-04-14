namespace Hydra.FileTransfer;

public record FileTransferInfo(string[] FileNames, long TotalBytes, bool IsSender)
{
    public int FileCount => FileNames.Length;
}

public interface IFileTransferDialog
{
    // show the dialog in "waiting for drop" state
    void ShowPending(FileTransferInfo info);

    // switch to active transfer state
    void ShowTransferring(FileTransferInfo info);

    // update progress bar and speed label
    void UpdateProgress(long bytesTransferred, double bytesPerSecond);

    // show completion briefly, then close
    void ShowCompleted();

    // show error message
    void ShowError(string message);

    // forcibly close the dialog
    void Close();

    // fires when the user clicks cancel
    event Action? CancelRequested;
}

public sealed class NullFileTransferDialog : IFileTransferDialog
{
    public void ShowPending(FileTransferInfo info) { }
    public void ShowTransferring(FileTransferInfo info) { }
    public void UpdateProgress(long bytesTransferred, double bytesPerSecond) { }
    public void ShowCompleted() { }
    public void ShowError(string message) { }
    public void Close() { }
#pragma warning disable CS0067
    public event Action? CancelRequested;
#pragma warning restore CS0067
}
