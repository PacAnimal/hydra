using Microsoft.Extensions.Logging;

namespace Hydra;

internal static class LogEvents
{
    // marks a message naming the user's files, which never leaves this machine's own log
    internal static readonly EventId FilePaths = new(7001, nameof(FilePaths));
}
