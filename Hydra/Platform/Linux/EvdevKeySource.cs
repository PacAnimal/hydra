using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

// an EvdevInputHandler in key-watch mode, its raw keys passed on as they come
internal sealed class EvdevKeySource : IEvdevKeySource
{
    private readonly EvdevInputHandler _handler;

    public EvdevKeySource(ILogger<EvdevInputHandler> log) : this(log, "/dev/input") { }

    // tests point it at a directory of links to devices they made
    internal EvdevKeySource(ILogger<EvdevInputHandler> log, string inputDir) =>
        _handler = new EvdevInputHandler(log) { InputDir = inputDir };

    public event Action<uint, bool>? Key;

    public bool Start() => _handler.StartKeyWatch((code, down) => Key?.Invoke(code, down));

    // EvdevInputHandler.DisposeAsync completes synchronously (it joins its own loop thread), so this waits on nothing
    // and no sync context is involved
    public void Dispose() => _handler.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
