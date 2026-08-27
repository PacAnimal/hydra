using System.Security.Cryptography;
using System.Text;

namespace Hydra.Management;

internal static class RemoteManagementCrypto
{
    internal static string RandomSecret(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    internal static string SignRequest(RemoteWireRequest request, string secret) => Sign(secret,
        request.Version.ToString(), request.RequestId.ToString("N"), request.ControllerId,
        request.TimestampUnixMs.ToString(), request.Nonce, request.Operation, request.Json);

    internal static string SignResponse(RemoteWireResponse response, string secret) => Sign(secret,
        response.Version.ToString(), response.RequestId.ToString("N"), response.TimestampUnixMs.ToString(),
        response.Success ? "1" : "0", response.Json ?? "", response.Error ?? "");

    internal static bool VerifyRequest(RemoteWireRequest request, string secret)
    {
        try { return FixedEquals(request.Signature, SignRequest(request with { Signature = "" }, secret)); }
        catch (FormatException) { return false; }
    }

    internal static bool VerifyResponse(RemoteWireResponse response, string secret)
    {
        try { return FixedEquals(response.Signature, SignResponse(response with { Signature = "" }, secret)); }
        catch (FormatException) { return false; }
    }

    internal static string HashPairingCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

    private static string Sign(string secret, params string[] fields)
    {
        var key = FromBase64Url(secret);
        using var hmac = new HMACSHA256(key);
        var data = Encoding.UTF8.GetBytes(string.Join('\n', fields.Select(field => Base64Url(Encoding.UTF8.GetBytes(field)))));
        return Base64Url(hmac.ComputeHash(data));
    }

    private static bool FixedEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(FromBase64Url(left), FromBase64Url(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
