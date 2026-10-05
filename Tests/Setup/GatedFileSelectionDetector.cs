using Hydra.FileTransfer;

namespace Tests.Setup;

// a file manager whose selection queries each block until released, as Finder does behind its consent prompt;
// one that ignores cancellation is an AppleScript call that cannot be interrupted
public sealed class GatedFileSelectionDetector(bool ignoresCancellation = false) : IFileSelectionDetector
{
    private readonly Lock _lock = new();
    private readonly List<Gate> _gates = [];
    private int _queries;

    public string FileManagerName => "Finder";
    public bool IsFileTransferSupported => true;

    public Task Queried => QueriedAt(0);

    public void Release(FileSelectionResult result) => Release(0, result);

    // the nth query (zero-based) has started
    public Task QueriedAt(int index) => GateAt(index).Queried.Task;

    public void Release(int index, FileSelectionResult result) => GateAt(index).Answer.TrySetResult(result);

    public Task Cancelled => CancelledAt(0);

    // the nth query (zero-based) saw its token cancelled while it waited
    public Task CancelledAt(int index) => GateAt(index).Cancelled.Task;

    public FileSelectionResult GetSelectedPaths(CancellationToken cancel)
    {
        int index;
        lock (_lock) index = _queries++;
        var gate = GateAt(index);
        gate.Queried.TrySetResult();
        using (cancel.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), gate.Cancelled))
            return (ignoresCancellation ? gate.Answer.Task : gate.Answer.Task.WaitAsync(cancel)).GetAwaiter().GetResult();
    }

    private Gate GateAt(int index)
    {
        lock (_lock)
        {
            while (_gates.Count <= index)
                _gates.Add(new Gate());
            return _gates[index];
        }
    }

    private sealed class Gate
    {
        public readonly TaskCompletionSource Queried = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<FileSelectionResult> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
