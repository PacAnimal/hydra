using Hydra.Management;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Management;

[TestFixture]
public class RemoteApplyStoreTests
{
    private string _root = null!;
    private string _configPath = null!;
    private string _original = null!;
    private readonly StartedServices _services = new();

    [SetUp]
    public void SetUp()
    {
        _root = TestPaths.FreshFixtureRoot(nameof(RemoteApplyStoreTests));
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
    public Task TearDown() => _services.StopAll();

    [Test]
    public async Task BeginAndConfirm_PersistsCandidateThenDeletesSecretBackup()
    {
        var (store, config, _) = CreateStore();
        var current = await config.ReadAsync();
        var candidate = Candidate();

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
        var (store, config, _) = CreateStore();
        var current = await config.ReadAsync();
        var candidate = ConfigSecretMask.Mask(_original).Replace("\"mouseScale\": 1.0", "\"mouseScale\": 1.5");
        _ = await store.BeginAsync(current.Revision, candidate, CancellationToken.None);

        await store.RollbackAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await File.ReadAllTextAsync(_configPath), Is.EqualTo(_original));
            Assert.That(await store.GetStateAsync(), Is.Null);
        }
    }

    [Test]
    public void ConnectivityChange_IsRejectedBeforeAnyWrite()
    {
        var (store, config, _) = CreateStore();
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
        var (store, config, _) = CreateStore();
        var candidate = ConfigSecretMask.Mask(original).Replace(ConfigSecretMask.Placeholder, "different-network");

        Assert.That(async () => await store.BeginAsync((await config.ReadAsync()).Revision, candidate, CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("connectivity changes"));
        Assert.That(File.Exists(Path.Combine(_root, ".hydra-remote-apply.json")), Is.False);
    }

    [Test]
    public void ModeChange_IsRejectedBeforeAnyWrite()
    {
        const string original = """
            {
              "name": "remote",
              "profiles": [{ "mode": "Slave", "networkConfig": "relay-secret" }]
            }
            """;
        File.WriteAllText(_configPath, original);
        var (store, config, _) = CreateStore();
        var candidate = ConfigSecretMask.Mask(original).Replace("\"mode\": \"Slave\"", "\"mode\": \"Master\"");

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
        var (store, config, _) = CreateStore();
        var current = await config.ReadAsync();
        var candidate = Candidate();
        _ = await store.BeginAsync(current.Revision, candidate, CancellationToken.None);
        var active = await config.ReadAsync();

        Assert.That(async () => await config.SaveAsync(active.Revision, active.Json, CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("awaiting confirmation"));
    }

    [Test]
    public async Task Rollback_RefusesToOverwriteUnexpectedExternalEdit()
    {
        var (store, config, _) = CreateStore();
        var current = await config.ReadAsync();
        var candidate = Candidate();
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
        var (store, config, lifetime) = CreateStore(confirmationWindow: TimeSpan.FromMilliseconds(100));
        var current = await config.ReadAsync();
        var candidate = Candidate();
        _ = await store.BeginAsync(current.Revision, candidate, CancellationToken.None);

        await _services.Start(store);
        await lifetime.RestartRequested.WaitAsync(TimeSpan.FromSeconds(3));
        await _services.StopAll();

        Assert.That(await File.ReadAllTextAsync(_configPath), Is.EqualTo(_original));
    }

    [Test]
    public async Task AutomaticRollback_RetriesAfterTransientMissingBackup()
    {
        var log = new RollbackFailureLog();
        var (store, config, lifetime) = CreateStore(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), log);
        var current = await config.ReadAsync();
        var candidate = Candidate();
        _ = await store.BeginAsync(current.Revision, candidate, CancellationToken.None);
        var backupPath = Path.Combine(_root, ".hydra-remote-backup.conf");
        File.Delete(backupPath);

        await _services.Start(store);
        await log.Failed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(lifetime.RestartRequested.IsCompleted, Is.False);
        await File.WriteAllTextAsync(backupPath, _original);

        await lifetime.RestartRequested.WaitAsync(TimeSpan.FromSeconds(5));
        await _services.StopAll();
        Assert.That(await File.ReadAllTextAsync(_configPath), Is.EqualTo(_original));
    }

    [Test]
    public async Task Rollback_WhenThePreviousConfigIsAlreadyBack_ClearsTheTransaction()
    {
        var (store, config, _) = CreateStore();
        var candidate = Candidate();
        _ = await store.BeginAsync((await config.ReadAsync()).Revision, candidate, CancellationToken.None);
        await File.WriteAllTextAsync(_configPath, _original);

        await store.RollbackAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await File.ReadAllTextAsync(_configPath), Is.EqualTo(_original));
            Assert.That(File.Exists(MarkerPath), Is.False);
            Assert.That(File.Exists(BackupPath), Is.False);
        }
    }

    [Test]
    public async Task Rollback_RefusesAnInvalidBackupAndKeepsTheTransaction()
    {
        var (store, config, _) = CreateStore();
        var candidate = Candidate();
        _ = await store.BeginAsync((await config.ReadAsync()).Revision, candidate, CancellationToken.None);
        var active = await File.ReadAllTextAsync(_configPath);
        await File.WriteAllTextAsync(BackupPath, "{");

        Assert.That(async () => await store.RollbackAsync(CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("backup is invalid"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await File.ReadAllTextAsync(_configPath), Is.EqualTo(active));
            Assert.That(File.Exists(MarkerPath), Is.True);
            Assert.That(File.Exists(BackupPath), Is.True);
        }
    }

    [Test]
    public async Task Rollback_WithoutABackup_Throws()
    {
        var (store, config, _) = CreateStore();
        var candidate = Candidate();
        _ = await store.BeginAsync((await config.ReadAsync()).Revision, candidate, CancellationToken.None);
        File.Delete(BackupPath);

        Assert.That(async () => await store.RollbackAsync(CancellationToken.None),
            Throws.TypeOf<IOException>().With.Message.Contains("backup is missing"));
    }

    [Test]
    public async Task Rollback_WithoutAMarker_Throws()
    {
        var (store, _, _) = CreateStore();
        await File.WriteAllTextAsync(BackupPath, _original);

        Assert.That(async () => await store.RollbackAsync(CancellationToken.None),
            Throws.TypeOf<IOException>().With.Message.Contains("marker is missing"));
    }

    [Test]
    public async Task Rollback_KeepsTheConfigFilesMode()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("unix permissions");
        const UnixFileMode shared = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        UnixMode.Set(_configPath, shared);
        var (store, config, _) = CreateStore();
        var candidate = Candidate();
        _ = await store.BeginAsync((await config.ReadAsync()).Revision, candidate, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(UnixMode.Of(_configPath), Is.EqualTo(shared), "the candidate save changed the mode");
            Assert.That(UnixMode.Of(BackupPath), Is.EqualTo(PrivateFile.OwnerOnly), "the backup holds the config's secrets");
            Assert.That(UnixMode.Of(MarkerPath), Is.EqualTo(PrivateFile.OwnerOnly));
        }

        await store.RollbackAsync(CancellationToken.None);

        Assert.That(UnixMode.Of(_configPath), Is.EqualTo(shared), "the rollback changed the mode");
    }

    [Test]
    public async Task StartupRestore_KeepsTheConfigFilesMode()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("unix permissions");
        const UnixFileMode shared = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        await WriteTransaction("{", _original, Expired);
        await File.WriteAllTextAsync(_configPath, "{");
        UnixMode.Set(_configPath, shared);

        var restored = await RemoteApplyStore.RestoreExpiredBeforeStartupAsync(_configPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored, Is.True);
            Assert.That(UnixMode.Of(_configPath), Is.EqualTo(shared));
        }
    }

    [Test]
    public async Task StartupRestore_RecreatesAMissingConfigOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("unix permissions");
        await WriteTransaction("candidate", _original, Expired);
        File.Delete(_configPath);

        var restored = await RemoteApplyStore.RestoreExpiredBeforeStartupAsync(_configPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored, Is.True);
            Assert.That(UnixMode.Of(_configPath), Is.EqualTo(PrivateFile.OwnerOnly));
        }
    }

    [Test]
    public async Task StartupRestore_WhenThePreviousConfigIsAlreadyBack_ClearsTheTransaction()
    {
        await WriteTransaction("candidate", _original, Expired);

        Assert.That(await RemoteApplyStore.RestoreExpiredBeforeStartupAsync(_configPath), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await File.ReadAllTextAsync(_configPath), Is.EqualTo(_original));
            Assert.That(File.Exists(MarkerPath), Is.False);
            Assert.That(File.Exists(BackupPath), Is.False);
        }
    }

    [Test]
    public async Task StartupRestore_LeavesAnUnexpiredTransactionAlone()
    {
        await WriteTransaction("{", _original, DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1));
        await File.WriteAllTextAsync(_configPath, "{");

        Assert.That(await RemoteApplyStore.RestoreExpiredBeforeStartupAsync(_configPath), Is.False);

        AssertUntouched("{");
    }

    [Test]
    public async Task StartupRestore_RefusesToOverwriteAnExternalEdit()
    {
        const string external = "{ \"name\": \"external repair\" }";
        await WriteTransaction("{", _original, Expired);
        await File.WriteAllTextAsync(_configPath, external);

        Assert.That(await RemoteApplyStore.RestoreExpiredBeforeStartupAsync(_configPath), Is.False);

        AssertUntouched(external);
    }

    [Test]
    public async Task StartupRestore_RefusesAnInvalidBackup()
    {
        await WriteTransaction("{", _original, Expired, backup: "{ not json");
        await File.WriteAllTextAsync(_configPath, "{");

        Assert.That(await RemoteApplyStore.RestoreExpiredBeforeStartupAsync(_configPath), Is.False);

        AssertUntouched("{");
    }

    [Test]
    public async Task StartupRestore_IgnoresAnUnreadableMarker()
    {
        await File.WriteAllTextAsync(MarkerPath, "not a marker");
        await File.WriteAllTextAsync(BackupPath, _original);
        await File.WriteAllTextAsync(_configPath, "{");

        Assert.That(await RemoteApplyStore.RestoreExpiredBeforeStartupAsync(_configPath), Is.False);

        AssertUntouched("{");
    }

    [Test]
    public async Task StartupRestore_WithoutABackup_DoesNothing()
    {
        await WriteTransaction("{", _original, Expired);
        File.Delete(BackupPath);
        await File.WriteAllTextAsync(_configPath, "{");

        Assert.That(await RemoteApplyStore.RestoreExpiredBeforeStartupAsync(_configPath), Is.False);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllText(_configPath), Is.EqualTo("{"));
            Assert.That(File.Exists(MarkerPath), Is.True);
        }
    }

    private static DateTimeOffset Expired => DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1);
    private string MarkerPath => Path.Combine(_root, ".hydra-remote-apply.json");
    private string BackupPath => Path.Combine(_root, ".hydra-remote-backup.conf");

    // a transaction from the candidate to the original, as BeginAsync leaves it
    private async Task WriteTransaction(string candidateJson, string previousJson, DateTimeOffset expiresAt, string? backup = null)
    {
        var marker = new RemoteApplyMarker(Guid.NewGuid(), TransactionalConfigStore.Revision(candidateJson),
            TransactionalConfigStore.Revision(previousJson), expiresAt);
        await File.WriteAllTextAsync(MarkerPath, ManagementJson.Serialize(marker));
        await File.WriteAllTextAsync(BackupPath, backup ?? previousJson);
    }

    private void AssertUntouched(string configJson)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllText(_configPath), Is.EqualTo(configJson));
            Assert.That(File.Exists(MarkerPath), Is.True);
            Assert.That(File.Exists(BackupPath), Is.True);
        }
    }

    // the safe edit most tests apply: debugMouse flipped on, secrets still masked
    private string Candidate() => ConfigSecretMask.Mask(_original).Replace("\"debugMouse\": false", "\"debugMouse\": true");

    private StoreSetup CreateStore(TimeSpan? confirmationWindow = null, TimeSpan? rollbackRetryDelay = null, ILogger<RemoteApplyStore>? log = null)
    {
        var runtime = new HydraRuntimeInfo(_configPath, DateTimeOffset.UtcNow);
        var config = new TransactionalConfigStore(runtime);
        var lifetime = new FakeLifetimeController();
        var store = new RemoteApplyStore(runtime, config, lifetime, log ?? NullLogger<RemoteApplyStore>.Instance, confirmationWindow, rollbackRetryDelay);
        return new StoreSetup(store, config, lifetime);
    }

    private sealed record StoreSetup(RemoteApplyStore Store, TransactionalConfigStore Config, FakeLifetimeController Lifetime);

    // completes when an automatic rollback attempt fails, which is the store reporting it will retry
    private sealed class RollbackFailureLog : ILogger<RemoteApplyStore>
    {
        private readonly TaskCompletionSource _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Failed => _failed.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Critical) _failed.TrySetResult();
        }
    }
}
