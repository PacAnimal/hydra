using Hydra.Management;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Management;

[TestFixture]
public class RemoteApplyStoreTests
{
    private string _root = null!;
    private string _configPath = null!;
    private string _original = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"hydra-remote-apply-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _configPath = Path.Combine(_root, "hydra.conf");
        _original = """
            {
              "name": "remote",
              "debugMouse": false,
              "profiles": [{ "mode": "Slave", "networkConfig": "relay-secret", "mouseScale": 1.0 }]
            }
            """;
        File.WriteAllText(_configPath, _original);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task BeginAndConfirm_PersistsCandidateThenDeletesSecretBackup()
    {
        var (store, config) = CreateStore();
        var current = await config.ReadAsync();
        var candidate = ConfigSecretMask.Mask(_original).Replace("\"debugMouse\": false", "\"debugMouse\": true");

        var accepted = await store.BeginAsync(current.Revision, candidate, CancellationToken.None);
        var active = await config.ReadAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(active.Json, Does.Contain("\"debugMouse\": true"));
            Assert.That(active.Json, Does.Contain("relay-secret"));
            Assert.That(File.Exists(Path.Combine(_root, ".hydra-remote-apply.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(_root, ".hydra-remote-backup.conf")), Is.True);
        }

        await store.ConfirmAsync(accepted.TransactionId, accepted.CandidateRevision, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await store.GetStateAsync(), Is.Null);
            Assert.That(File.Exists(Path.Combine(_root, ".hydra-remote-apply.json")), Is.False);
            Assert.That(File.Exists(Path.Combine(_root, ".hydra-remote-backup.conf")), Is.False);
        }
    }

    [Test]
    public async Task Rollback_RestoresExactPreviousConfig()
    {
        var (store, config) = CreateStore();
        var current = await config.ReadAsync();
        var candidate = ConfigSecretMask.Mask(_original).Replace("\"mouseScale\": 1.0", "\"mouseScale\": 1.5");
        _ = await store.BeginAsync(current.Revision, candidate, CancellationToken.None);

        await store.RollbackAsync(CancellationToken.None);

        Assert.That(await File.ReadAllTextAsync(_configPath), Is.EqualTo(_original));
        Assert.That(await store.GetStateAsync(), Is.Null);
    }

    [Test]
    public void ConnectivityChange_IsRejectedBeforeAnyWrite()
    {
        var (store, config) = CreateStore();
        var candidate = ConfigSecretMask.Mask(_original).Replace(ConfigSecretMask.Placeholder, "different-network");

        Assert.That(async () => await store.BeginAsync((await config.ReadAsync()).Revision, candidate, CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("connectivity changes"));
        Assert.That(File.Exists(Path.Combine(_root, ".hydra-remote-apply.json")), Is.False);
    }

    [Test]
    public void ConnectivityChange_WithDifferentJsonCasing_IsRejectedBeforeAnyWrite()
    {
        const string original = """
            {
              "Name": "remote",
              "Profiles": [{ "Mode": "Slave", "NetworkConfig": "relay-secret" }]
            }
            """;
        File.WriteAllText(_configPath, original);
        var (store, config) = CreateStore();
        var candidate = ConfigSecretMask.Mask(original).Replace(ConfigSecretMask.Placeholder, "different-network");

        Assert.That(async () => await store.BeginAsync((await config.ReadAsync()).Revision, candidate, CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("connectivity changes"));
        Assert.That(File.Exists(Path.Combine(_root, ".hydra-remote-apply.json")), Is.False);
    }

    [Test]
    public async Task ExpiredMarker_RestoresBackupBeforeInvalidConfigBootstrap()
    {
        var marker = new RemoteApplyMarker(Guid.NewGuid(), TransactionalConfigStore.Revision("{"), TransactionalConfigStore.Revision(_original),
            DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
        await File.WriteAllTextAsync(Path.Combine(_root, ".hydra-remote-apply.json"), ManagementJson.Serialize(marker));
        await File.WriteAllTextAsync(Path.Combine(_root, ".hydra-remote-backup.conf"), _original);
        await File.WriteAllTextAsync(_configPath, "{");

        var restored = await RemoteApplyStore.RestoreExpiredBeforeStartupAsync(_configPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored, Is.True);
            Assert.That(await File.ReadAllTextAsync(_configPath), Is.EqualTo(_original));
            Assert.That(File.Exists(Path.Combine(_root, ".hydra-remote-apply.json")), Is.False);
            Assert.That(File.Exists(Path.Combine(_root, ".hydra-remote-backup.conf")), Is.False);
        }
    }

    [Test]
    public async Task ExpiredMarker_RestoresBackupWhenConfigFileIsMissing()
    {
        var marker = new RemoteApplyMarker(Guid.NewGuid(), "candidate", TransactionalConfigStore.Revision(_original),
            DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
        await File.WriteAllTextAsync(Path.Combine(_root, ".hydra-remote-apply.json"), ManagementJson.Serialize(marker));
        await File.WriteAllTextAsync(Path.Combine(_root, ".hydra-remote-backup.conf"), _original);
        File.Delete(_configPath);

        var restored = await RemoteApplyStore.RestoreExpiredBeforeStartupAsync(_configPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored, Is.True);
            Assert.That(await File.ReadAllTextAsync(_configPath), Is.EqualTo(_original));
            Assert.That(File.Exists(Path.Combine(_root, ".hydra-remote-apply.json")), Is.False);
            Assert.That(File.Exists(Path.Combine(_root, ".hydra-remote-backup.conf")), Is.False);
        }
    }

    [Test]
    public async Task PendingTransaction_BlocksLocalTuiSave()
    {
        var (store, config) = CreateStore();
        var current = await config.ReadAsync();
        var candidate = ConfigSecretMask.Mask(_original).Replace("\"debugMouse\": false", "\"debugMouse\": true");
        _ = await store.BeginAsync(current.Revision, candidate, CancellationToken.None);
        var active = await config.ReadAsync();

        Assert.That(async () => await config.SaveAsync(active.Revision, active.Json, CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("awaiting confirmation"));
    }

    [Test]
    public async Task Rollback_RefusesToOverwriteUnexpectedExternalEdit()
    {
        var (store, config) = CreateStore();
        var current = await config.ReadAsync();
        var candidate = ConfigSecretMask.Mask(_original).Replace("\"debugMouse\": false", "\"debugMouse\": true");
        _ = await store.BeginAsync(current.Revision, candidate, CancellationToken.None);
        const string external = "{ \"name\": \"external repair\" }";
        await File.WriteAllTextAsync(_configPath, external);

        Assert.That(async () => await store.RollbackAsync(CancellationToken.None),
            Throws.TypeOf<IOException>().With.Message.Contains("refused to overwrite"));
        Assert.That(await File.ReadAllTextAsync(_configPath), Is.EqualTo(external));
    }

    [Test]
    public async Task UnconfirmedCandidate_IsAutomaticallyRolledBackAndRestartRequested()
    {
        var runtime = new HydraRuntimeInfo(_configPath, DateTimeOffset.UtcNow);
        var config = new TransactionalConfigStore(runtime);
        var lifetime = new FakeLifetimeController();
        var store = new RemoteApplyStore(runtime, config, lifetime, NullLogger<RemoteApplyStore>.Instance,
            TimeSpan.FromMilliseconds(100));
        var current = await config.ReadAsync();
        var candidate = ConfigSecretMask.Mask(_original).Replace("\"debugMouse\": false", "\"debugMouse\": true");
        _ = await store.BeginAsync(current.Revision, candidate, CancellationToken.None);

        await store.StartAsync(CancellationToken.None);
        await lifetime.RestartRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await store.StopAsync(CancellationToken.None);

        Assert.That(await File.ReadAllTextAsync(_configPath), Is.EqualTo(_original));
    }

    [Test]
    public async Task AutomaticRollback_RetriesAfterTransientMissingBackup()
    {
        var runtime = new HydraRuntimeInfo(_configPath, DateTimeOffset.UtcNow);
        var config = new TransactionalConfigStore(runtime);
        var lifetime = new FakeLifetimeController();
        var store = new RemoteApplyStore(runtime, config, lifetime, NullLogger<RemoteApplyStore>.Instance,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));
        var current = await config.ReadAsync();
        var candidate = ConfigSecretMask.Mask(_original).Replace("\"debugMouse\": false", "\"debugMouse\": true");
        _ = await store.BeginAsync(current.Revision, candidate, CancellationToken.None);
        var backupPath = Path.Combine(_root, ".hydra-remote-backup.conf");
        File.Delete(backupPath);

        await store.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(1_200));
        Assert.That(lifetime.RestartRequested.Task.IsCompleted, Is.False);
        await File.WriteAllTextAsync(backupPath, _original);

        await lifetime.RestartRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await store.StopAsync(CancellationToken.None);
        Assert.That(await File.ReadAllTextAsync(_configPath), Is.EqualTo(_original));
    }

    private (RemoteApplyStore Store, TransactionalConfigStore Config) CreateStore()
    {
        var runtime = new HydraRuntimeInfo(_configPath, DateTimeOffset.UtcNow);
        var config = new TransactionalConfigStore(runtime);
        var lifetime = new FakeLifetimeController();
        return (new RemoteApplyStore(runtime, config, lifetime, NullLogger<RemoteApplyStore>.Instance), config);
    }

    private sealed class FakeLifetimeController : IHydraLifetimeController
    {
        internal TaskCompletionSource RestartRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void RestartAfterResponse() => RestartRequested.TrySetResult();
        public CommandResult ShutdownAfterResponse() => new(true, "Shutdown requested.");
    }
}
