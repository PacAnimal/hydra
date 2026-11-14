using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace Hydra.Platform.Linux.Wayland;

// org.freedesktop.portal.Clipboard on an InputCapture or RemoteDesktop session whose Start enabled it. Content moves
// through pipes the portal hands out: SelectionRead gives the read end of the current selection, and a SelectionTransfer
// asks this side to write its own advertised selection into SelectionWrite's end, then report through SelectionWriteDone.
// Both ends are polled in short slices against a deadline, so a peer that stops reading or writing never holds a thread.
internal sealed class PortalClipboard : IAsyncDisposable, IPortalClipboard
{
    private const int PollSliceMs = 100;
    // <poll.h>'s POLLOUT
    private const short PollOut = 0x0004;
    // what a pipe that polls writable takes without blocking (PIPE_BUF: 4096 on Linux, 512 elsewhere POSIX)
    private static readonly int PipeAtomicBytes = OperatingSystem.IsLinux() ? 4096 : 512;
    // how long a requester may take to read what Hydra serves
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    private readonly PortalBus _bus;
    private readonly ObjectPath _session;
    private readonly ILogger _log;
    private readonly Task<IDisposable[]> _matches;
    private int _disposed;

    internal PortalClipboard(PortalBus bus, ObjectPath session, ILogger log)
    {
        _bus = bus;
        _session = session;
        _log = log;
        _matches = Watch();
    }

    public event Func<string, uint, Task>? SelectionTransfer;

    public event Action<IReadOnlyList<string>, bool>? SelectionOwnerChanged;

    // asks for the clipboard on a session not yet started, when the portal has the interface; the Start response's
    // clipboard_enabled says whether it was granted
    internal static async Task RequestClipboard(PortalBus bus, ObjectPath session)
    {
        if (await bus.Has(PortalBus.ClipboardInterface))
            await bus.Connection.CallMethodAsync(bus.SessionCall(PortalBus.ClipboardInterface, "RequestClipboard", session, []));
    }

    private async Task<IDisposable[]> Watch()
    {
        var owner = await _bus.WatchSessionSignal(PortalBus.ClipboardInterface, "SelectionOwnerChanged", _session, options =>
        {
            var types = options.TryGetValue("mime_types", out var t) ? t.GetArray<string>() : [];
            var isOwner = options.TryGetValue("session_is_owner", out var o) && o.GetBool();
            SelectionOwnerChanged?.Invoke(types, isOwner);
        });
        var ours = _session.ToString();
        var rule = new MatchRule { Type = MessageType.Signal, Interface = PortalBus.ClipboardInterface, Member = "SelectionTransfer", Path = PortalBus.DesktopPath };
        var transfer = await _bus.Watch(rule, (message, _) =>
            {
                var reader = message.GetBodyReader();
                return new TransferRequest(reader.ReadObjectPathAsString(), reader.ReadString(), reader.ReadUInt32());
            },
            request =>
            {
                if (request.Session == ours) _ = Transfer(request.MimeType, request.Serial);
            });
        return [owner, transfer];
    }

    private async Task Transfer(string mimeType, uint serial)
    {
        var handler = SelectionTransfer;
        try
        {
            if (handler == null) await SelectionWriteDone(serial, false);
            else await handler(mimeType, serial);
        }
        catch (Exception e)
        {
            if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning(e, "Answering the portal's request for {MimeType} failed", mimeType);
        }
    }

    public async Task SetSelection(IReadOnlyList<string> mimeTypes)
    {
        await _matches;
        var options = new Dictionary<string, VariantValue> { ["mime_types"] = VariantValue.Array(mimeTypes.ToArray()) };
        await _bus.Connection.CallMethodAsync(_bus.SessionCall(PortalBus.ClipboardInterface, "SetSelection", _session, options));
    }

    // the write end for a SelectionTransfer's serial, which the caller owns and closes
    internal async Task<SafeHandle> SelectionWrite(uint serial)
    {
        return await _bus.CallForHandle(_bus.Message(PortalBus.DesktopPath, PortalBus.ClipboardInterface, "SelectionWrite", "ou", (ref MessageWriter writer) =>
        {
            writer.WriteObjectPath(_session);
            writer.WriteUInt32(serial);
        }));
    }

    public Task SelectionWriteDone(uint serial, bool success) =>
        _bus.Connection.CallMethodAsync(_bus.Message(PortalBus.DesktopPath, PortalBus.ClipboardInterface, "SelectionWriteDone", "oub", (ref MessageWriter writer) =>
        {
            writer.WriteObjectPath(_session);
            writer.WriteUInt32(serial);
            writer.WriteBool(success);
        }));

    // the read end of the current selection as mimeType, which the caller owns and closes
    internal async Task<SafeHandle> SelectionRead(string mimeType)
    {
        return await _bus.CallForHandle(_bus.Message(PortalBus.DesktopPath, PortalBus.ClipboardInterface, "SelectionRead", "os", (ref MessageWriter writer) =>
        {
            writer.WriteObjectPath(_session);
            writer.WriteString(mimeType);
        }));
    }

    // The selection as mimeType, read to EOF; null when it runs past maxBytes. The pipe is polled in short slices so a
    // source that never writes or never closes ends at the timeout instead of holding a thread in read().
    public async Task<byte[]?> Read(string mimeType, int maxBytes, TimeSpan timeout, CancellationToken cancel)
    {
        using var handle = await SelectionRead(mimeType);
        return await Task.Run(() => ReadToEnd(handle, maxBytes, DateTime.UtcNow + timeout, cancel), cancel);
    }

    private static byte[]? ReadToEnd(SafeHandle handle, int maxBytes, DateTime deadline, CancellationToken cancel)
    {
        using var stream = new FileStream(new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false), FileAccess.Read, bufferSize: 0);
        return WaylandMime.ReadCapped(stream, maxBytes,
            () => WaitFor(handle, NativeMethods.POLLIN, deadline, "The clipboard source did not finish writing in time", cancel));
    }

    // Polls the pipe in slices until it is ready for events (or hung up, which the read or write then reports); throws
    // at the deadline. Poll is not bound with SetLastError, so a failed poll (EINTR among them) is simply polled again
    // until the deadline, never told apart by errno.
    private static void WaitFor(SafeHandle handle, short events, DateTime deadline, string late, CancellationToken cancel)
    {
        var poll = new PollFd { Fd = (int)handle.DangerousGetHandle(), Events = events };
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) throw new TimeoutException(late);
            poll.Revents = 0;
            if (NativeMethods.Poll(ref poll, 1, (int)Math.Min(PollSliceMs, Math.Ceiling(left.TotalMilliseconds))) > 0) return;
        }
    }

    // Writes data into the transfer's pipe, then reports whether it all went. Each write is at most what a writable pipe
    // takes at once, after a poll, so a requester that stops reading is given up on at the deadline: the pipe is closed
    // and the transfer reported failed.
    public async Task Write(uint serial, byte[] data)
    {
        bool success;
        try
        {
            using var handle = await SelectionWrite(serial);
            await Task.Run(() => WriteAll(handle, data, DateTime.UtcNow + WriteTimeout));
            success = true;
        }
        catch (Exception e) when (e is IOException or DBusExceptionBase or UnauthorizedAccessException or TimeoutException)
        {
            if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug(e, "Writing {Bytes} clipboard bytes to the portal failed", data.Length);
            success = false;
        }
        await SelectionWriteDone(serial, success);
    }

    internal static void WriteAll(SafeHandle handle, byte[] data, DateTime deadline)
    {
        using var stream = new FileStream(new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false), FileAccess.Write, bufferSize: 0);
        for (var offset = 0; offset < data.Length; offset += PipeAtomicBytes)
        {
            WaitFor(handle, PollOut, deadline, "The clipboard requester did not read in time", CancellationToken.None);
            stream.Write(data, offset, Math.Min(PipeAtomicBytes, data.Length - offset));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            foreach (var match in await _matches) match.Dispose();
        }
        catch (DBusExceptionBase e)
        {
            // the matches were never added (the bus went first), so there is nothing to remove
            if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("Portal clipboard signals were never watched: {Error}", e.Message);
        }
    }

    private sealed record TransferRequest(string Session, string MimeType, uint Serial);
}
