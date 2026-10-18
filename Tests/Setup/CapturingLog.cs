using Microsoft.Extensions.Logging;

namespace Tests.Setup;

// records every entry at or above minimum, for a test that reads what was logged
public sealed class CapturingLog<T>(LogLevel minimum = LogLevel.Trace) : ILogger<T>
{
    private readonly List<CapturedEntry> _entries = [];
    private readonly SemaphoreSlim _logged = new(0);

    public CapturedEntry[] Entries
    {
        get
        {
            lock (_entries) return [.. _entries];
        }
    }

    public string[] Messages => [.. Entries.Select(e => e.Message)];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        lock (_entries) _entries.Add(new CapturedEntry(logLevel, formatter(state, exception), eventId));
        _logged.Release();
    }

    // the first entry matching, waiting for it to be logged
    public async Task<CapturedEntry> WaitFor(Func<CapturedEntry, bool> match, int timeoutMs = 15000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            while (true)
            {
                if (Entries.FirstOrDefault(match) is { } entry) return entry;
                await _logged.WaitAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out waiting for a matching log entry");
        }
    }
}

public sealed record CapturedEntry(LogLevel Level, string Message, EventId EventId);
