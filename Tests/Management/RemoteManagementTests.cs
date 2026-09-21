using Hydra.Management;
using Hydra.Relay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Management;

[TestFixture]
public class RemoteManagementTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"hydra-remote-management-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void SignedRequest_RejectsPayloadTampering()
    {
        var secret = RemoteManagementCrypto.RandomSecret();
        var request = new RemoteWireRequest(1, Guid.NewGuid(), "controller", 123, "nonce", "config.get", "{}", "");
        request = request with { Signature = RemoteManagementCrypto.SignRequest(request, secret) };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RemoteManagementCrypto.VerifyRequest(request, secret), Is.True);
            Assert.That(RemoteManagementCrypto.VerifyRequest(request with { Operation = "config.apply" }, secret), Is.False);
            Assert.That(RemoteManagementCrypto.VerifyRequest(request, RemoteManagementCrypto.RandomSecret()), Is.False);
            Assert.That(RemoteManagementCrypto.VerifyRequest(request, "not-base64!"), Is.False);
        }
    }

    [Test]
    public async Task PairingCode_IsSingleUseAndStoredAsAHash()
    {
        var configPath = ConfigPath("store");
        var store = new RemoteManagementStore(configPath);
        var code = await store.CreatePairingCodeAsync();

        Assert.That(await store.ConsumePairingCodeAsync(code, CancellationToken.None), Is.True);
        Assert.That(await store.ConsumePairingCodeAsync(code, CancellationToken.None), Is.False);

        var stateJson = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(configPath)!, ".hydra-management.json"));
        Assert.That(stateJson, Does.Not.Contain(code));
        if (!OperatingSystem.IsWindows())
            Assert.That(File.GetUnixFileMode(Path.Combine(Path.GetDirectoryName(configPath)!, ".hydra-management.json")),
                Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
    }

    [Test]
    public async Task ConcurrentStoreInstances_PreserveEveryPairingCode()
    {
        var configPath = ConfigPath("concurrent-store");
        var first = new RemoteManagementStore(configPath);
        var second = new RemoteManagementStore(configPath);
        var codes = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(index => (index & 1) == 0
                ? first.CreatePairingCodeAsync()
                : second.CreatePairingCodeAsync()));
        var reader = new RemoteManagementStore(configPath);

        foreach (var code in codes)
            Assert.That(await reader.ConsumePairingCodeAsync(code, CancellationToken.None), Is.True);

        if (!OperatingSystem.IsWindows())
            Assert.That(File.GetUnixFileMode(Path.Combine(Path.GetDirectoryName(configPath)!, ".hydra-management.lock")),
                Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
    }

    [Test]
    public async Task RequestNonce_IsRejectedAfterServiceRestart()
    {
        var configPath = ConfigPath("replay");
        var first = new RemoteManagementStore(configPath);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.That(await first.RememberRequestAsync("controller", "nonce", now, CancellationToken.None), Is.True);

        var reloaded = new RemoteManagementStore(configPath);
        Assert.That(await reloaded.RememberRequestAsync("controller", "nonce", now, CancellationToken.None), Is.False);
    }

    [Test]
    public void RuntimeStore_CanBeConstructedByDependencyInjection()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new HydraRuntimeInfo(ConfigPath("dependency-injection"), DateTimeOffset.UtcNow));
        services.AddSingleton<RemoteManagementStore>();
        using var provider = services.BuildServiceProvider();

        Assert.That(provider.GetRequiredService<RemoteManagementStore>(), Is.Not.Null);
    }

    [Test]
    public async Task PairedController_CanReadOnlyMaskedConfigAndValidateAnEdit()
    {
        var localPath = ConfigPath("local");
        var remotePath = ConfigPath("remote");
        var (localRelay, remoteRelay) = LinkedRelay.Create("local", "remote");
        var localStore = new RemoteManagementStore(localPath);
        var remoteStore = new RemoteManagementStore(remotePath);
        var local = Service(localRelay, localStore, localPath);
        var remote = Service(remoteRelay, remoteStore, remotePath);
        await local.StartAsync(CancellationToken.None);
        await remote.StartAsync(CancellationToken.None);

        var code = await remoteStore.CreatePairingCodeAsync();
        var paired = await local.PairAsync(new RemotePairRequest("remote", code), CancellationToken.None);
        var document = await local.GetConfigAsync("remote", CancellationToken.None);
        var validation = await local.ValidateConfigAsync(new RemoteValidateRequest("remote", document.Json), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(paired.Paired, Is.True);
            Assert.That(document.Host, Is.EqualTo("remote"));
            Assert.That(document.Json, Does.Contain(ConfigSecretMask.Placeholder));
            Assert.That(document.Json, Does.Not.Contain("relay-secret"));
            Assert.That(validation.Valid, Is.True);
        }

        await local.StopAsync(CancellationToken.None);
        await remote.StopAsync(CancellationToken.None);
    }

    [Test]
    public void UnpairedHost_IsRejectedBeforeSending()
    {
        var path = ConfigPath("local");
        var relay = new LinkedRelay("local");
        var service = Service(relay, new RemoteManagementStore(path), path);

        Assert.That(async () => await service.GetConfigAsync("remote", CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("not paired"));
    }

    [Test]
    public async Task InvalidPairingCode_ReturnsImmediateRejection()
    {
        var localPath = ConfigPath("invalid-pair-local");
        var remotePath = ConfigPath("invalid-pair-remote");
        var (localRelay, remoteRelay) = LinkedRelay.Create("local", "remote");
        var local = Service(localRelay, new RemoteManagementStore(localPath), localPath);
        var remote = Service(remoteRelay, new RemoteManagementStore(remotePath), remotePath);
        await local.StartAsync(CancellationToken.None);
        await remote.StartAsync(CancellationToken.None);

        var result = await local.PairAsync(new RemotePairRequest("remote", RemoteManagementCrypto.RandomSecret()),
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Paired, Is.False);
            Assert.That(result.Message, Does.Contain("invalid"));
        }
        await local.StopAsync(CancellationToken.None);
        await remote.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task PairedController_CanApplyAndConfirmSafeCandidate()
    {
        var localPath = ConfigPath("local");
        var remotePath = ConfigPath("remote");
        var (localRelay, remoteRelay) = LinkedRelay.Create("local", "remote");
        var localStore = new RemoteManagementStore(localPath);
        var remoteStore = new RemoteManagementStore(remotePath);
        var remoteLifetime = new FakeLifetimeController();
        var local = Service(localRelay, localStore, localPath);
        var remote = Service(remoteRelay, remoteStore, remotePath, remoteLifetime);
        await local.StartAsync(CancellationToken.None);
        await remote.StartAsync(CancellationToken.None);

        var code = await remoteStore.CreatePairingCodeAsync();
        _ = await local.PairAsync(new RemotePairRequest("remote", code), CancellationToken.None);
        var document = await local.GetConfigAsync("remote", CancellationToken.None);
        var edited = document.Json.Replace("\"mode\": \"Slave\"", "\"mode\": \"Slave\",\n      \"mouseScale\": 1.5");

        var accepted = await local.ApplyConfigAsync(new RemoteApplyRequest("remote", document.Revision, edited), CancellationToken.None);
        await local.ConfirmConfigAsync(new RemoteConfirmRequest("remote", accepted.TransactionId, accepted.CandidateRevision), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await File.ReadAllTextAsync(remotePath), Does.Contain("\"mouseScale\": 1.5"));
            Assert.That(remoteLifetime.RestartRequests, Is.EqualTo(1));
            Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(remotePath)!, ".hydra-remote-apply.json")), Is.False);
            Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(remotePath)!, ".hydra-remote-backup.conf")), Is.False);
        }

        await local.StopAsync(CancellationToken.None);
        await remote.StopAsync(CancellationToken.None);
    }

    private static RemoteManagementService Service(IRelaySender relay, RemoteManagementStore store, string configPath,
        FakeLifetimeController? lifetime = null)
    {
        var runtime = new HydraRuntimeInfo(configPath, DateTimeOffset.UtcNow);
        var config = new TransactionalConfigStore(runtime);
        lifetime ??= new FakeLifetimeController();
        var apply = new RemoteApplyStore(runtime, config, lifetime, NullLogger<RemoteApplyStore>.Instance);
        return new RemoteManagementService(relay, store, config, apply, lifetime,
            NullLogger<RemoteManagementService>.Instance);
    }

    private string ConfigPath(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "hydra.conf");
        File.WriteAllText(path, $$"""
            {
              "name": "{{name}}",
              "profiles": [{ "mode": "Slave", "networkConfig": "relay-secret" }]
            }
            """);
        return path;
    }

    private sealed class LinkedRelay(string host) : IRelaySender
    {
        private readonly string _host = host;
        private LinkedRelay? _other;
        public bool IsConnected => _other != null;
        public event Func<string[], Task>? PeersChanged { add { } remove { } }
        public event Func<string, MessageKind, ReadOnlyMemory<byte>, Task>? MessageReceived;
        public event Func<Task>? Disconnected { add { } remove { } }

        internal static (LinkedRelay Left, LinkedRelay Right) Create(string leftHost, string rightHost)
        {
            var left = new LinkedRelay(leftHost);
            var right = new LinkedRelay(rightHost);
            left._other = right;
            right._other = left;
            return (left, right);
        }

        public void Send(string[] targetHosts, byte[] payload)
        {
            var other = _other;
            if (other == null || !targetHosts.Contains(other._host, StringComparer.OrdinalIgnoreCase)) return;
            var decoded = MessageSerializer.Decode(payload);
            var handler = other.MessageReceived;
            if (handler != null) _ = handler(_host, decoded.Kind, decoded.Bytes);
        }
    }

    private sealed class FakeLifetimeController : IHydraLifetimeController
    {
        internal int RestartRequests;
        public void RestartAfterResponse() => RestartRequests++;
        public CommandResult ShutdownAfterResponse() => new(true, "Shutdown requested.");
    }
    /// <summary>
    /// A reader in another store instance does not make a concurrent writer LOSE its write.
    ///
    /// <para><b>The platforms disagree completely here, and only one of them is honest.</b> A reader opens
    /// the state file without <c>FILE_SHARE_DELETE</c>, so on Windows a writer replacing it at that instant
    /// gets <c>ERROR_ACCESS_DENIED</c> and the write fails outright — the pairing code it was recording is
    /// gone, and the user has already been shown it. On POSIX the rename succeeds and the reader finishes
    /// off the old inode, so the same defect is completely invisible. It was found on the Windows lane, as
    /// an intermittent failure of the concurrency test next to this one.</para>
    ///
    /// <para>Every write is asserted to SUCCEED and every code to survive, because "no exception" and "the
    /// data is there" are two claims and the interesting failure only breaks the first on one platform.</para>
    /// </summary>
    [Test]
    public async Task AConcurrentReaderDoesNotCostAWriterItsWrite()
    {
        var configPath = ConfigPath("reader-vs-writer");
        var writer = new RemoteManagementStore(configPath);
        var reader = new RemoteManagementStore(configPath);

        await writer.CreatePairingCodeAsync();

        var reading = new CancellationTokenSource();
        // The TOKEN is captured, not the source: a struct the loop owns, so the reader cannot touch a
        // disposed CancellationTokenSource however the awaits below unwind.
        var token = reading.Token;
        var reads = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
                await reader.GetTargetAsync("anything", CancellationToken.None);
        }, CancellationToken.None);

        var codes = new List<string>();
        for (var i = 0; i < 20; i++) codes.Add(await writer.CreatePairingCodeAsync());

        await reading.CancelAsync();
        await reads;
        reading.Dispose();

        var checker = new RemoteManagementStore(configPath);
        foreach (var code in codes)
            Assert.That(await checker.ConsumePairingCodeAsync(code, CancellationToken.None), Is.True,
                "a write was lost while another instance was reading — on Windows the reader's handle refuses the replace");
    }

    /// <summary>
    /// A replace waits out a holder it cannot serialise with, instead of failing the write.
    ///
    /// <para>Our own reader is fixed by taking the lock; this is the one that is NOT ours — a virus scanner
    /// or the search indexer opening the file we just closed, for a few milliseconds. It is the likeliest
    /// explanation for the intermittent lane failure that started this, and it cannot be locked against
    /// because it is another program entirely.</para>
    ///
    /// <para>Deterministic, not timed: the handle is released only once the store has actually been forced
    /// to retry, so the test proves the wait happened rather than hoping it did.</para>
    /// </summary>
    [Test]
    public async Task AReplaceWaitsOutAHolderRatherThanFailingTheWrite()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("a POSIX rename over an open file succeeds, so there is nothing here to wait for");

        var configPath = ConfigPath("held-destination");
        var store = new RemoteManagementStore(configPath);
        await store.CreatePairingCodeAsync();

        var statePath = Path.Combine(Path.GetDirectoryName(configPath)!, ".hydra-management.json");
        var holder = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var write = store.CreatePairingCodeAsync();
        var waited = SpinWait.SpinUntil(() => Volatile.Read(ref store.ReplaceRetries) > 0, TimeSpan.FromSeconds(10));
        holder.Dispose();

        var code = await write;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(waited, Is.True, "the replace never had to wait, so this test proved nothing about waiting");
            Assert.That(await store.ConsumePairingCodeAsync(code, CancellationToken.None), Is.True,
                "the write did not land once the holder let go");
        }
    }
}
