using Microsoft.Extensions.Logging;

namespace Hydra.Management;

internal sealed class ManagementLogBuffer : ILoggerProvider
{
    internal const int Capacity = 2000;
    private readonly Lock _lock = new();
    private readonly Queue<ManagementLogEntry> _entries = new(Capacity);
    private long _cursor;
    private long _dropped;

    public ILogger CreateLogger(string categoryName) => new BufferLogger(this, categoryName);
    public void Dispose() { }

    internal ManagementLogPage Read(long afterCursor)
    {
        lock (_lock)
            return new ManagementLogPage(_cursor, _dropped, [.. _entries.Where(e => e.Cursor > afterCursor)]);
    }

    private void Add(LogLevel level, string category, string message)
    {
        if (category.Contains("FileTransfer", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Clipboard", StringComparison.OrdinalIgnoreCase)
            || message.Contains("file(s) selected", StringComparison.OrdinalIgnoreCase))
            return;

        lock (_lock)
        {
            if (_entries.Count == Capacity)
            {
                _entries.Dequeue();
                _dropped++;
            }
            _entries.Enqueue(new ManagementLogEntry(++_cursor, DateTimeOffset.UtcNow, level.ToString(), category, message));
        }
    }

    private sealed class BufferLogger(ManagementLogBuffer owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception != null) message = $"{message}: {exception.GetType().Name}: {exception.Message}";
            owner.Add(logLevel, category, message);
        }
    }
}
