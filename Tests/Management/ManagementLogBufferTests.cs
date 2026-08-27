using Hydra.Management;
using Microsoft.Extensions.Logging;

namespace Tests.Management;

public class ManagementLogBufferTests
{
    [Test]
    public void CapturesInformationAndFiltersSensitiveCategories()
    {
        using var buffer = new ManagementLogBuffer();
        var normal = buffer.CreateLogger("Hydra.Relay");
        var sensitive = buffer.CreateLogger("Hydra.FileTransfer.FileTransferService");
        var routing = buffer.CreateLogger("Hydra.Screen.InputRouter");

        normal.LogInformation("Connected to relay");
        sensitive.LogInformation("selected /private/file");
        routing.LogInformation("Copy hotkey: 1 file(s) selected locally: /private/secret.txt");
        var page = buffer.Read(0);

        Assert.Multiple(() =>
        {
            Assert.That(page.Entries, Has.Count.EqualTo(1));
            Assert.That(page.Entries[0].Message, Is.EqualTo("Connected to relay"));
            Assert.That(page.Entries[0].Category, Is.EqualTo("Hydra.Relay"));
            Assert.That(page.Entries.Select(entry => entry.Message), Has.None.Contains("/private/secret.txt"));
        });
    }

    [Test]
    public void IsBoundedAndReportsDrops()
    {
        using var buffer = new ManagementLogBuffer();
        var logger = buffer.CreateLogger("Hydra.Test");
        for (var i = 0; i < 2005; i++) logger.LogInformation("entry {Index}", i);

        var page = buffer.Read(0);

        Assert.Multiple(() =>
        {
            Assert.That(page.Entries, Has.Count.EqualTo(2000));
            Assert.That(page.Dropped, Is.EqualTo(5));
            Assert.That(page.LatestCursor, Is.EqualTo(2005));
        });
    }
}
