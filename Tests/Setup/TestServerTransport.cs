using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.TestHost;

namespace Tests.Setup;

public static class TestServerTransport
{
    // keeps both transports in memory; left to itself, SignalR's WebSocket dials a real socket to localhost:80
    // first, which Windows refuses only after its SYN retries
    public static void UseTestServer(this HttpConnectionOptions options, TestServer server)
    {
        options.HttpMessageHandlerFactory = _ => server.CreateHandler();
        options.WebSocketFactory = async (context, cancel) => await server.CreateWebSocketClient().ConnectAsync(context.Uri, cancel);
    }
}
