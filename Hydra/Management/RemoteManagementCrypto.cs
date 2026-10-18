using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Cathedral.Extensions;

namespace Hydra.Management;

internal static class RemoteManagementCrypto
{
    internal static string RandomSecret(int bytes = 32) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

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

    internal static string HashPairingCode(string code) => code.GetSha256Hash();

    internal static bool HashesEqual(string left, string right) => FixedEquals(left, right, Convert.FromHexString);

    private static string Sign(string secret, params string[] fields)
    {
        var data = Encoding.UTF8.GetBytes(string.Join('\n', fields.Select(field => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(field)))));
        return Base64Url.EncodeToString(HMACSHA256.HashData(Base64Url.DecodeFromChars(secret), data));
    }

    private static bool FixedEquals(string left, string right) => FixedEquals(left, right, text => Base64Url.DecodeFromChars(text));

    // a fixed-time compare of what two encodings decode to; false when either does not decode
    private static bool FixedEquals(string left, string right, Func<string, byte[]> decode)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(decode(left), decode(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
