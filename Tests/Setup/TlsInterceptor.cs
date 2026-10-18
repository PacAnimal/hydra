using Hydra.Relay;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Tests.Setup;

/// <summary>
/// Stands between a client and the embedded relay with a cert of its own. The first <c>passThrough</c> connections reach
/// the relay untouched, so the client sees the relay's cert; every later one is answered with the interceptor's cert and a
/// forged binding proof, and each is reported: the first line of the request it carried, or null when the client refused
/// the handshake.
/// </summary>
public sealed class TlsInterceptor : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _certificate = EmbeddedRelayTls.CreateCertificate();
    private readonly CancellationTokenSource _stop = new();
    private readonly NotificationQueue<string?> _intercepted = new();
    private volatile int _relayPort;
    private readonly Task _accepting;
    private int _passThrough;

    public TlsInterceptor(int relayPort, int passThrough = 0)
    {
        _relayPort = relayPort;
        _passThrough = passThrough;
        _listener.Start();
        _accepting = Accept();
    }

    // where pass-through connections go from now on, as for a relay restarted on another port
    public void RetargetTo(int relayPort) => _relayPort = relayPort;

    public string Url => $"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    // the next intercepted connection's request line, or null for a refused handshake
    public Task<string?> NextIntercepted() => _intercepted.Next("an intercepted connection");

    private async Task Accept()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await _listener.AcceptSocketAsync(_stop.Token); }
            catch (Exception) { return; }

            _ = Interlocked.Decrement(ref _passThrough) >= 0 ? PassThrough(socket) : Intercept(socket);
        }
    }

    private async Task PassThrough(Socket client)
    {
        using var relay = new TcpClient();
        using var clientStream = new NetworkStream(client, ownsSocket: true);
        try
        {
            await relay.ConnectAsync(IPAddress.Loopback, _relayPort, _stop.Token);
            var relayStream = relay.GetStream();
            await Task.WhenAny(clientStream.CopyToAsync(relayStream, _stop.Token), relayStream.CopyToAsync(clientStream, _stop.Token));
        }
        catch (Exception) { /* either side closing ends it */ }
    }

    private async Task Intercept(Socket client)
    {
        await using var tls = new SslStream(new NetworkStream(client, ownsSocket: true));
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token);
        }
        catch (Exception)
        {
            _intercepted.Push(null);
            return;
        }

        string? line = null;
        try
        {
            // a forged answer to the binding, so a client that skipped checking it would go on to the hub here
            var reader = new StreamReader(tls, Encoding.ASCII);
            line = await reader.ReadLineAsync(_stop.Token);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
            await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: {EmbeddedRelayTls.ProofLength}\r\n\r\n"), _stop.Token);
            await tls.WriteAsync(new byte[EmbeddedRelayTls.ProofLength], _stop.Token);
        }
        catch (Exception) { /* the client hanging up is its answer */ }
        _intercepted.Push(line);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        _listener.Dispose();
        _certificate.Dispose();
        _stop.Dispose();
    }
}
