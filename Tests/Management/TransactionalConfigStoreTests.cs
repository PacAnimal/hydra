using Hydra.Management;

namespace Tests.Management;

public class TransactionalConfigStoreTests
{
    private string _directory = null!;
    private string _path = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "management-config", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "hydra.conf");
        File.WriteAllText(_path, Valid("Home"));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public async Task Save_ValidatesAndUpdatesRevision()
    {
        var store = new TransactionalConfigStore(new HydraRuntimeInfo(_path, DateTimeOffset.UtcNow));
        var before = await store.ReadAsync();

        var after = await store.SaveAsync(before.Revision, Valid("Work"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(after.Revision, Is.Not.EqualTo(before.Revision));
            Assert.That(File.ReadAllText(_path), Does.Contain("Work"));
            Assert.That(TransactionalConfigStore.Validate(after.Json).Valid, Is.True);
        });
    }

    [Test]
    public async Task Save_RejectsConcurrentEditWithoutOverwriting()
    {
        var store = new TransactionalConfigStore(new HydraRuntimeInfo(_path, DateTimeOffset.UtcNow));
        var before = await store.ReadAsync();
        File.WriteAllText(_path, Valid("External"));

        Assert.That(async () => await store.SaveAsync(before.Revision, Valid("TUI"), CancellationToken.None),
            Throws.InvalidOperationException.With.Message.Contains("changed outside"));
        Assert.That(File.ReadAllText(_path), Does.Contain("External"));
    }

    [Test]
    public async Task Save_RejectsInvalidJsonWithoutOverwriting()
    {
        var store = new TransactionalConfigStore(new HydraRuntimeInfo(_path, DateTimeOffset.UtcNow));
        var before = await store.ReadAsync();

        Assert.That(async () => await store.SaveAsync(before.Revision, "{ broken", CancellationToken.None),
            Throws.InvalidOperationException);
        Assert.That(File.ReadAllText(_path), Is.EqualTo(before.Json));
    }

    private static string Valid(string profile) => $$"""
        {
          "name": "test-host",
          "profiles": [{
            "profileName": "{{profile}}",
            "mode": "Slave",
            "embeddedStyx": { "server": "http://127.0.0.1:5000", "password": "test" }
          }]
        }
        """;
}
