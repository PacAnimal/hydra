using System.Buffers.Binary;
using Hydra.Management;

namespace Tests.Management;

public class ManagementFramingTests
{
    [Test]
    public async Task RoundTripsRequest()
    {
        await using var stream = new MemoryStream();
        var request = new ManagementRequest("status", "{\"x\":1}");
        await ManagementFraming.WriteAsync(stream, request, CancellationToken.None);
        stream.Position = 0;

        var actual = await ManagementFraming.ReadAsync<ManagementRequest>(stream, CancellationToken.None);

        Assert.That(actual, Is.EqualTo(request));
    }

    [Test]
    public void RejectsOversizedFrameBeforeAllocation()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, ManagementProtocol.MaxMessageBytes + 1);
        using var stream = new MemoryStream(header);

        Assert.That(async () => await ManagementFraming.ReadAsync<ManagementRequest>(stream, CancellationToken.None),
            Throws.TypeOf<InvalidDataException>());
    }
}
