using System.Security.Cryptography;
using System.Text;
using Hydra.Config;

namespace Hydra.Management;

internal sealed class TransactionalConfigStore(HydraRuntimeInfo runtime)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    internal async Task<ConfigDocument> ReadAsync(CancellationToken cancel = default)
    {
        var json = await File.ReadAllTextAsync(runtime.ConfigPath, cancel);
        return new ConfigDocument(runtime.ConfigPath, Revision(json), json);
    }

    internal static ConfigValidation Validate(string json)
    {
        try
        {
            _ = HydraConfigFile.Parse(json, "<tui>");
            return new ConfigValidation(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            return new ConfigValidation(false, ex.Message);
        }
    }

    internal async Task<ConfigDocument> SaveAsync(string expectedRevision, string json, CancellationToken cancel)
    {
        var validation = Validate(json);
        if (!validation.Valid) throw new InvalidOperationException(validation.Error);

        await _writeLock.WaitAsync(cancel);
        try
        {
            var current = await File.ReadAllTextAsync(runtime.ConfigPath, cancel);
            if (!Revision(current).Equals(expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("hydra.conf changed outside the TUI. Reload before saving.");

            var directory = Path.GetDirectoryName(runtime.ConfigPath)!;
            var temp = Path.Combine(directory, $".{Path.GetFileName(runtime.ConfigPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(json);
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 bufferSize: 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, cancel);
                    await stream.FlushAsync(cancel);
                    stream.Flush(flushToDisk: true);
                }
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temp, File.GetUnixFileMode(runtime.ConfigPath));
                _ = HydraConfigFile.Parse(await File.ReadAllTextAsync(temp, cancel), temp);
                File.Replace(temp, runtime.ConfigPath, null);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }

            return new ConfigDocument(runtime.ConfigPath, Revision(json), json);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal static string Revision(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
}
