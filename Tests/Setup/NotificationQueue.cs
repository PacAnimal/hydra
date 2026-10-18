using System.Threading.Channels;

namespace Tests.Setup;

/// <summary>
/// Ordered replacement for a counting semaphore paired with a last-value field. Callbacks push, waiters pop,
/// so a test reading two notifications in a row gets the first one rather than whatever the second callback
/// overwrote while the waiter was waking up.
/// </summary>
public sealed class NotificationQueue<T>
{
    private readonly Channel<T> _channel = Channel.CreateUnbounded<T>();

    public void Push(T item) => _channel.Writer.TryWrite(item);

    // pushed but not yet read
    public int Count => _channel.Reader.Count;

    public Task<T> Next(string what) => Next(TestBound.Bound, what);

    public async Task<T> Next(TimeSpan timeout, string what)
    {
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            return await _channel.Reader.ReadAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Timed out waiting for {what}");
        }
    }
}
