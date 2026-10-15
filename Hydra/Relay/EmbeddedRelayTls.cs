using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Styx.Services;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Hydra.Relay;

/// <summary>
/// The embedded relay's TLS: a self-signed cert made on every start, bound to the relay password by a mutual proof.
/// The client proves first, for the cert it was shown, so a stranger who can reach the port learns nothing to guess
/// against; the server answers only a valid proof, and only then does the client trust that cert.
/// </summary>
internal static class EmbeddedRelayTls
{
    public const string BindingPath = "/binding";
    public const int ProofLength = 32;
    private const int Iterations = 100_000;
    private static readonly TimeSpan PlainHttpWarningInterval = TimeSpan.FromMinutes(10);
    private static readonly byte[] ClientLabel = "hydra-embedded-binding/client"u8.ToArray();
    private static readonly byte[] ServerLabel = "hydra-embedded-binding/server"u8.ToArray();

    // what an older Hydra on plain http reads, as the status of its negotiate request
    private static readonly byte[] UpgradeRequired = Encoding.ASCII.GetBytes(
        "HTTP/1.1 426 Upgrade Required\r\nUpgrade: TLS/1.2, HTTP/1.1\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");

    public static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Hydra embedded relay", key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        if (!OperatingSystem.IsWindows()) return certificate;

        // schannel cannot serve the ephemeral key CreateSelfSigned returns, nor an EphemeralKeySet import, so the key goes
        // through the user's key store until the cert is disposed
        using (certificate)
        {
            var pfx = certificate.Export(X509ContentType.Pfx);
            try { return X509CertificateLoader.LoadPkcs12(pfx, null); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
    }

    public static byte[] FingerprintOf(X509Certificate certificate) => SHA256.HashData(certificate.GetRawCertData());

    public static Proofs ProofsFor(string password, byte[] fingerprint)
    {
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), fingerprint, Iterations, HashAlgorithmName.SHA256, ProofLength);
        return new Proofs(fingerprint, HMACSHA256.HashData(key, ClientLabel), HMACSHA256.HashData(key, ServerLabel));
    }

    public static bool Matches(ReadOnlySpan<byte> given, byte[] expected) => CryptographicOperations.FixedTimeEquals(given, expected);

    // the embedded relay only speaks https, whatever the config says, and an address with no scheme means it too
    public static string WithHttps(string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "https://" + url["http://".Length..];
        return url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url;
    }

    /// <summary>
    /// Answers a plain-HTTP request with 426 and a warning naming the likely cause, an older Hydra. Kestrel's TLS would
    /// only log the failed handshake at debug. Registered ahead of <c>UseHttps</c>, it peeks without consuming.
    /// </summary>
    public static ListenOptions RefusePlainHttp(this ListenOptions listenOptions, ILogger log)
    {
        var warned = new BoundedLru<string, DateTimeOffset>(1000);
        listenOptions.Use(next => async connection =>
        {
            // an http request starts with its method, in capitals; a silent, reset or closed connection is just dropped
            var first = await PeekFirstByte(connection);
            if (first == null) return;
            if (first is < (byte)'A' or > (byte)'Z')
            {
                await next(connection);
                return;
            }

            // once per address a while, since an old peer retries every few seconds
            var address = (connection.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "an unknown address";
            var now = DateTimeOffset.UtcNow;
            if (warned.AddOrUpdate(FailureLimiter.KeyOf(address), last => now - last < PlainHttpWarningInterval ? last : now) == now)
                log.LogWarning("Plain http from {Address} refused: the embedded relay needs TLS, so that machine probably runs a Hydra older than 0.3. Update it", address);

            try { await connection.Transport.Output.WriteAsync(UpgradeRequired, connection.ConnectionClosed); }
            catch (Exception) { /* the peer may already be gone */ }
        });
        return listenOptions;
    }

    private static async Task<byte?> PeekFirstByte(ConnectionContext connection)
    {
        var input = connection.Transport.Input;
        using var firstBytes = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);
        firstBytes.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var read = await input.ReadAsync(firstBytes.Token);
            byte? first = read.Buffer.IsEmpty ? null : read.Buffer.FirstSpan[0];
            input.AdvanceTo(read.Buffer.Start);
            return first;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Proves the password to the relay for the cert this connection was shown, and returns that cert's fingerprint once
    /// the relay has proven it back. The proof is computed after the handshake, as the body is written, because it is
    /// bound to the cert the handshake presented. Null means the cert passed ordinary validation, as a standalone Styx
    /// behind a real cert does: that relay is trusted like any https site, and the request is dropped before it carries
    /// anything. <paramref name="trusts"/> says which validation results count as that; tests replace it.
    /// </summary>
    public static async Task<byte[]?> ProveRelay(string server, string password, SocketsHttpHandler handler, TimeSpan timeout, CancellationToken cancel,
        Func<SslPolicyErrors, bool>? trusts = null)
    {
        trusts ??= errors => errors == SslPolicyErrors.None;
        byte[]? presented = null;
        var trustedByCa = false;
        handler.MaxConnectionsPerServer = 1;
        handler.AllowAutoRedirect = false;
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            presented = certificate == null ? null : FingerprintOf(certificate);
            trustedByCa = trusts(errors);
            return presented != null;
        };

        Proofs? proofs = null;
        using var http = new HttpClient(handler);
        http.Timeout = timeout;
        http.MaxResponseContentBufferSize = ProofLength;
        using var content = new ProofContent(() => trustedByCa
            ? throw new TrustedByCa()
            : (proofs = ProofsFor(password, presented ?? throw new InvalidOperationException("No cert presented"))).Client);

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(server.TrimEnd('/') + BindingPath, content, cancel);
        }
        catch (Exception ex) when (ex is TrustedByCa || (trustedByCa && ex is HttpRequestException { InnerException: TrustedByCa }))
        {
            return null;
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            throw new HttpRequestException($"TLS with the embedded relay failed ({ex.InnerMessage()}). A relay on a Hydra older than 0.3 speaks plain http: update that machine");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new HttpRequestException("The embedded relay refused this machine's proof: a wrong password, or something in between");
            response.EnsureSuccessStatusCode();

            var answer = await response.Content.ReadAsByteArrayAsync(cancel);
            if (proofs == null || !Matches(answer, proofs.Server))
                throw new HttpRequestException("The embedded relay did not prove it knows the password: something may be in between");
            return proofs.Fingerprint;
        }
    }

    // accepts only the cert the binding proved; a restarted relay has a new one, and must prove it again
    public static void PinTo(this SocketsHttpHandler handler, byte[] fingerprint) =>
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate != null && FingerprintOf(certificate).AsSpan().SequenceEqual(fingerprint);

    internal sealed record Proofs(byte[] Fingerprint, byte[] Client, byte[] Server);

    // aborts the binding of a relay whose cert ordinary validation already trusts, so it is sent nothing
    private sealed class TrustedByCa : Exception;

    // writes a proof computed only once the connection, and so the cert, exists
    private sealed class ProofContent(Func<byte[]> proof) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(proof()).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = ProofLength;
            return true;
        }
    }
}
