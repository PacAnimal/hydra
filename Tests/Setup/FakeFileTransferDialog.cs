using Hydra.FileTransfer;

namespace Tests.Setup;

public sealed class FakeFileTransferDialog : IFileTransferDialog
{
    public string LastState { get; private set; } = "none";
    public string? LastError { get; private set; }
    public int ProgressUpdates { get; private set; }

    // runs as the transfer is shown, i.e. after the send slot is claimed and before streaming starts
    public Action? OnShowTransferring { get; set; }

    // runs as the receiving extractor starts a file, on the extractor's thread
    public Action? OnSetCurrentFile { get; set; }

    // runs as an outcome (completed, error or closed) is reported, on the reporting thread
    public Action? OnOutcome { get; set; }

    private void Report(string state)
    {
        LastState = state;
        OnOutcome?.Invoke();
    }

    public void ShowTransferring(FileTransferInfo info)
    {
        LastState = "transferring";
        OnShowTransferring?.Invoke();
    }
    public void SetCurrentFile(string fileName) => OnSetCurrentFile?.Invoke();
    public void UpdateProgress(long bytesTransferred, double bytesPerSecond) => ProgressUpdates++;
    public void ShowCompleted() => Report("completed");
    public void ShowError(string message) { LastError = message; Report("error"); }
    public void Close() => Report("closed");
    public event Action? CancelRequested;
    public void TriggerCancel() => CancelRequested?.Invoke();
}
