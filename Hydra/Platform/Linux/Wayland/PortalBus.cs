using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace Hydra.Platform.Linux.Wayland;

// The session bus as the XDG desktop portal sees it. Every portal call that takes a handle_token goes through Request:
// its Response is watched before the call is sent, on any request path, so neither a fast reply nor a portal older
// than 0.9 answering on a path of its own is missed. Every signal watch goes through Watch, so no reader or handler can
// throw into the connection (Tmds disconnects it then).
internal sealed class PortalBus : IAsyncDisposable
{
    internal const string Destination = "org.freedesktop.portal.Desktop";
    internal const string DesktopPath = "/org/freedesktop/portal/desktop";
    internal const string ClipboardInterface = "org.freedesktop.portal.Clipboard";
    // persist_mode: the grant is kept until the user revokes it
    internal const uint PersistUntilRevoked = 2;
    private const string RequestInterface = "org.freedesktop.portal.Request";
    private const string SessionInterface = "org.freedesktop.portal.Session";

    // completion notifications, so a dropped bus fails a pending request and reaches a watch's lost
    private const ObserverFlags WatchFlags =
        ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnConnectionFailed | ObserverFlags.EmitOnReaderFailed;

    private readonly ILogger _log;
    private int _tokens;

    private PortalBus(DBusConnection connection, string uniqueName, ILogger log)
    {
        Connection = connection;
        UniqueName = uniqueName;
        _log = log;
    }

    internal DBusConnection Connection { get; }
    internal string UniqueName { get; }

    // justWritten: the desktop file for appId was written this run, so the portal may not have indexed it yet and an
    // "App info not found" is retried for a moment (RegisterBackoff) before Hydra goes on without an app id
    internal static async Task<PortalBus> Connect(string appId, bool justWritten, ILogger log, CancellationToken cancel, TimeProvider? time = null)
    {
        var address = DBusAddress.Session ?? throw new PortalException("No D-Bus session bus address (DBUS_SESSION_BUS_ADDRESS unset)");
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(cancel);
            var uniqueName = connection.UniqueName ?? throw new PortalException("The session bus gave no unique name");
            var bus = new PortalBus(connection, uniqueName, log);
            var delays = justWritten ? RegisterBackoff.Delays(RegisterBackoff.First, RegisterBackoff.Total) : [];
            await bus.Register(appId, delays, time ?? TimeProvider.System, cancel);
            return bus;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // tells the portal which app this unsandboxed host process is, before any other portal call
    private async Task Register(string appId, IEnumerable<TimeSpan> delays, TimeProvider time, CancellationToken cancel)
    {
        if (await RegisterBackoff.Retry(() => TryRegister(appId), delays, time, cancel)) return;
        // expected without an installed .desktop file for appId; only the app's name in portal dialogs is lost
        if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("The desktop portal knows no desktop file for {AppId}; continuing without an app id", appId);
    }

    // false only when the portal knows no desktop file for appId (yet); every other outcome is settled here
    private async Task<bool> TryRegister(string appId)
    {
        var call = Message(DesktopPath, "org.freedesktop.host.portal.Registry", "Register", "sa{sv}", (ref MessageWriter writer) =>
        {
            writer.WriteString(appId);
            writer.WriteDictionary(new Dictionary<string, VariantValue>());
        });
        try
        {
            await Connection.CallMethodAsync(call);
            if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("Registered with the desktop portal as {AppId}", appId);
        }
        catch (DBusErrorReplyException e) when (e.ErrorName is "org.freedesktop.DBus.Error.UnknownMethod" or "org.freedesktop.DBus.Error.UnknownInterface")
        {
            if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("The desktop portal has no host registry; continuing without an app id");
        }
        catch (DBusErrorReplyException e) when (e.ErrorName == "org.freedesktop.portal.Error.Failed" && e.ErrorMessage.Contains("App info not found", StringComparison.Ordinal))
        {
            return false;
        }
        catch (DBusErrorReplyException e)
        {
            // registration only names the app in the portal's dialogs and permission store, so a refusal is not fatal
            if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning("The desktop portal refused to register {AppId}: {Error}", appId, e.Message);
        }
        return true;
    }

    // a fresh token for a handle_token or session_handle_token
    internal string NextToken() => "hydra" + Interlocked.Increment(ref _tokens);

    // The portal's answer to call, given the request token. A timeout closes the request (and any dialog it shows) and
    // throws TimeoutException; a cancel closes it and throws OperationCanceledException.
    internal async Task<PortalResponse> Request(Func<string, MessageBuffer> call, CancellationToken cancel, TimeSpan? timeout = null)
    {
        var token = NextToken();
        var answer = new RequestAnswer($"{DesktopPath}/request/{UniqueName.TrimStart(':').Replace('.', '_')}/{token}");
        using var match = await WatchResponses(answer);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        if (timeout is { } limit) deadline.CancelAfter(limit);
        try
        {
            var handle = await Connection.CallMethodAsync(call(token), (message, _) => message.GetBodyReader().ReadObjectPathAsString())
                .WaitAsync(deadline.Token);
            answer.Answering(handle);
            return await answer.Response.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await CloseRequest(answer.Path);
            if (cancel.IsCancellationRequested) throw;
            throw new TimeoutException($"The desktop portal did not answer request {answer.Path} within {timeout}");
        }
    }

    // every Response to this connection, held until the request's path is known
    private Task<IDisposable> WatchResponses(RequestAnswer answer)
    {
        var rule = new MatchRule { Type = MessageType.Signal, Interface = RequestInterface, Member = "Response" };
        return Watch(rule, (message, _) =>
            {
                var reader = message.GetBodyReader();
                var code = reader.ReadUInt32();
                return new RequestSignal(message.PathAsString ?? "", new PortalResponse(code, reader.ReadDictionaryOfStringToVariantValue()));
            },
            signal => answer.Answered(signal.Path, signal.Response),
            lost: e => answer.Lost(e));
    }

    private async Task CloseRequest(string path)
    {
        try
        {
            await Connection.CallMethodAsync(Message(path, RequestInterface, "Close"));
        }
        catch (DBusExceptionBase e)
        {
            // the request may already have answered or gone with its dialog; nothing is left to close then
            if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("Closing portal request {Path} failed: {Error}", path, e.Message);
        }
    }

    internal async Task<uint> Version(string interfaceName)
    {
        var call = Message(DesktopPath, "org.freedesktop.DBus.Properties", "Get", "ss", (ref MessageWriter writer) =>
        {
            writer.WriteString(interfaceName);
            writer.WriteString("version");
        });
        return await Connection.CallMethodAsync(call, (message, _) => message.GetBodyReader().ReadVariantValue().GetUInt32());
    }

    // whether the portal implements interfaceName at all
    internal async Task<bool> Has(string interfaceName)
    {
        try
        {
            await Version(interfaceName);
            return true;
        }
        catch (DBusErrorReplyException e)
        {
            if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("The desktop portal has no {Interface}: {Error}", interfaceName, e.Message);
            return false;
        }
    }

    // a method call to the portal; body writes the arguments, which signature describes
    internal MessageBuffer Message(string path, string interfaceName, string member, string? signature = null, WriteBody? body = null)
    {
        var writer = Connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Destination, path, interfaceName, member, signature);
            body?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    // a call on the portal object of the common (o session_handle, a{sv} options) shape
    internal MessageBuffer SessionCall(string interfaceName, string member, ObjectPath session, Dictionary<string, VariantValue> options) =>
        Message(DesktopPath, interfaceName, member, "oa{sv}", (ref MessageWriter writer) =>
        {
            writer.WriteObjectPath(session);
            writer.WriteDictionary(options);
        });

    // Watches signals matching rule. A reader or handler that throws is logged and the signal dropped, never let into
    // Tmds, which would disconnect the whole connection. lost, when given, hears of a closed or failed connection once.
    internal async Task<IDisposable> Watch<T>(MatchRule rule, MessageValueReader<T> reader, Action<T> handler, Action<Exception>? lost = null)
    {
        var what = $"{rule.Interface}.{rule.Member}";
        return await Connection.AddMatchAsync(rule, (message, state) =>
            {
                try
                {
                    return new Signal<T>(reader(message, state), null);
                }
                catch (Exception e)
                {
                    return new Signal<T>(default, e);
                }
            },
            n =>
            {
                if (n.IsCompletion)
                {
                    if (n.Type is NotificationType.ConnectionClosed or NotificationType.ConnectionFailed or NotificationType.ReaderFailed)
                        Guard(what, () => lost?.Invoke(n.CompletionReason));
                    return;
                }
                if (!n.HasValue) return;
                if (n.Value.Failure is { } failure)
                {
                    if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning(failure, "A portal {Signal} signal could not be read; dropped", what);
                    return;
                }
                Guard(what, () => handler(n.Value.Value!));
            },
            emitOnCapturedContext: false, WatchFlags);
    }

    private void Guard(string what, Action handler)
    {
        try
        {
            handler();
        }
        catch (Exception e)
        {
            if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning(e, "Handling a portal {Signal} signal failed", what);
        }
    }

    // the session's Closed, also raised when the bus goes, since a session cannot outlive its connection
    internal Task<IDisposable> WatchSessionClosed(ObjectPath session, Action closed)
    {
        var rule = new MatchRule { Type = MessageType.Signal, Interface = SessionInterface, Member = "Closed", Path = session.ToString() };
        return Watch(rule, (_, _) => true, _ => closed(), lost: e =>
        {
            if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning(e, "The session bus went; portal session {Session} is gone with it", session);
            closed();
        });
    }

    // a portal signal of the (o session_handle, a{sv} options) shape, passed on only when it is for session
    internal Task<IDisposable> WatchSessionSignal(string interfaceName, string member, ObjectPath session,
        Action<Dictionary<string, VariantValue>> handler)
    {
        var ours = session.ToString();
        var rule = new MatchRule { Type = MessageType.Signal, Interface = interfaceName, Member = member, Path = DesktopPath };
        return Watch(rule, (message, _) =>
            {
                var reader = message.GetBodyReader();
                return new SessionSignal(reader.ReadObjectPathAsString(), reader.ReadDictionaryOfStringToVariantValue());
            },
            signal =>
            {
                if (signal.Session == ours) handler(signal.Options);
            });
    }

    // one portal session's watches, and its closing: what every session class holds the same way
    internal async Task<SessionWatch> WatchSession(ObjectPath session, Action closed) =>
        new(this, session, await WatchSessionClosed(session, closed));

    // a method of the (o session_handle, a{sv} options) shape that answers with a file descriptor, now the caller's
    internal Task<SafeFileHandle> CallForHandle(MessageBuffer call) =>
        Connection.CallMethodAsync(call, (message, _) => message.GetBodyReader().ReadHandle<SafeFileHandle>());

    // the EIS socket of a session on interfaceName (InputCapture, RemoteDesktop), which the caller owns and closes
    internal async Task<SafeHandle> ConnectToEis(string interfaceName, ObjectPath session) =>
        await CallForHandle(SessionCall(interfaceName, "ConnectToEIS", session, []));

    // the Start response's grants, logged alike for every session kind
    internal static void LogStarted(ILogger log, string what, ObjectPath session, bool clipboardEnabled, string? restoreToken)
    {
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("{What} session {Session} started (clipboard {Clipboard}, restore token {Restorable})",
                what, session, clipboardEnabled ? "enabled" : "not enabled", restoreToken != null ? "granted" : "not granted");
    }

    internal async Task CloseSession(ObjectPath session)
    {
        try
        {
            await Connection.CallMethodAsync(Message(session.ToString(), SessionInterface, "Close"));
        }
        catch (DBusExceptionBase e)
        {
            // the portal or compositor may have closed the session first, or the bus is gone; either way it is over
            if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("Closing portal session {Session} failed: {Error}", session, e.Message);
        }
    }

    // the session handle from a CreateSession response, an object path except on RemoteDesktop, where it is a string
    internal static ObjectPath SessionHandle(Dictionary<string, VariantValue> results)
    {
        if (!results.TryGetValue("session_handle", out var handle)) throw new PortalException("The portal returned no session handle");
        return handle.Type == VariantValueType.String ? new ObjectPath(handle.GetString()) : handle.GetObjectPath();
    }

    public ValueTask DisposeAsync()
    {
        Connection.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record SessionSignal(string Session, Dictionary<string, VariantValue> Options);

    private sealed record RequestSignal(string Path, PortalResponse Response);

    // a signal as its reader left it: the value, or why it could not be read
    private readonly record struct Signal<T>(T? Value, Exception? Failure);

    // One request's Response. Responses arriving before the call says the request's path (a pre-0.9 portal's own path)
    // are held, so whichever path the portal names, its answer is never missed
    internal sealed class RequestAnswer(string expected)
    {
        private readonly TaskCompletionSource<PortalResponse> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _lock = new();
        private readonly Dictionary<string, PortalResponse> _early = [];
        private string? _path;

        internal Task<PortalResponse> Response => _response.Task;

        // the request's path: the one the call returned, else the one predicted
        internal string Path
        {
            get
            {
                lock (_lock) return _path ?? expected;
            }
        }

        internal void Answered(string path, PortalResponse response)
        {
            lock (_lock)
            {
                if (_path == null)
                {
                    if (path == expected) _response.TrySetResult(response);
                    else _early.TryAdd(path, response);
                    return;
                }
                if (path == _path) _response.TrySetResult(response);
            }
        }

        // the call returned the request's path
        internal void Answering(string path)
        {
            lock (_lock)
            {
                _path = path;
                if (_early.TryGetValue(path, out var response)) _response.TrySetResult(response);
                _early.Clear();
            }
        }

        internal void Lost(Exception reason) => _response.TrySetException(reason);
    }
}

// a portal session's Closed watch and its other signal watches, all dropped, and the session closed, on dispose
internal sealed class SessionWatch(PortalBus bus, ObjectPath session, IDisposable closed) : IAsyncDisposable
{
    private readonly List<IDisposable> _matches = [closed];
    private int _disposed;

    internal void Add(IDisposable match) => _matches.Add(match);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var match in _matches) match.Dispose();
        await bus.CloseSession(session);
    }
}

internal delegate void WriteBody(ref MessageWriter writer);

internal sealed record PortalResponse(uint Code, Dictionary<string, VariantValue> Results)
{
    internal string? String(string key) => Results.TryGetValue(key, out var value) ? value.GetString() : null;

    internal bool Bool(string key) => Results.TryGetValue(key, out var value) && value.GetBool();

    // throws unless the user allowed the request
    internal PortalResponse Check(string what) => Code switch
    {
        0 => this,
        1 => throw new PortalException($"{what} was cancelled"),
        _ => throw new PortalException($"{what} failed (portal response {Code})"),
    };
}

internal sealed class PortalException : Exception
{
    internal PortalException(string message) : base(message) { }

    internal PortalException(string message, Exception inner) : base(message, inner) { }
}
