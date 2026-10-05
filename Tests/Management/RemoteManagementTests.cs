using Hydra.Management;
using Hydra.Relay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Management;

[TestFixture]
public class RemoteManagementTests
{
    private string _root = null!;
    private readonly StartedServices _services = new();

    [SetUp]
    public void SetUp() => _root = TestPaths.FreshFixtureRoot(nameof(RemoteManagementTests));

    [TearDown]
    public Task TearDown() => _services.StopAll();

    // stored hashes in .hydra-management.json must still match after an upgrade
    [Test]
    public void PairingCodeHash_IsLowercaseHexSha256()
    {
        Assert.That(RemoteManagementCrypto.HashPairingCode("hydra-pair"),
            Is.EqualTo("7b9cf80fb8a605ebf303d7c977dfc55745f15f52cfe4b1ea8403645a015df549"));
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

    // the private DACL names the console user of the last write, so a new console user needs it written afresh
    [Test]
    public async Task Restamp_MakesTheSidecarPrivateAgain_KeepingItsContents()
    {
        var configPath = ConfigPath("restamp");
        var store = new RemoteManagementStore(configPath);
        var code = await store.CreatePairingCodeAsync();
        var sidecar = new ConfigDir(configPath).ManagementState;
        var json = await File.ReadAllTextAsync(sidecar);
        // replaced by a plain write, which takes the directory's ACL or the umask's mode
        File.Delete(sidecar);
        await File.WriteAllTextAsync(sidecar, json);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(sidecar, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        await store.Restamp(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            if (OperatingSystem.IsWindows())
                Assert.That(new FileInfo(sidecar).GetAccessControl().AreAccessRulesProtected, Is.True, "the sidecar still inherits the directory's ACL");
            else
                Assert.That(File.GetUnixFileMode(sidecar), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
            Assert.That(await store.ConsumePairingCodeAsync(code, CancellationToken.None), Is.True);
        }
    }

    [Test]
    public async Task Restamp_CreatesNoSidecar()
    {
        var configPath = ConfigPath("restamp-absent");

        await new RemoteManagementStore(configPath).Restamp(CancellationToken.None);

        Assert.That(File.Exists(new ConfigDir(configPath).ManagementState), Is.False);
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
        // Logging, because the host has it and this store now takes an ILogger<T> — a retry that waits out
        // another program holding these files is otherwise invisible. Registering it here keeps the graph
        // this asserts about the same shape as the one Program.cs builds; leaving it out would test a
        // graph nothing runs.
        services.AddLogging();
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
        var local = await _services.Start(Service(localRelay, localStore, localPath));
        await _services.Start(Service(remoteRelay, remoteStore, remotePath));

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
        var local = await _services.Start(Service(localRelay, new RemoteManagementStore(localPath), localPath));
        await _services.Start(Service(remoteRelay, new RemoteManagementStore(remotePath), remotePath));

        var result = await local.PairAsync(new RemotePairRequest("remote", RemoteManagementCrypto.RandomSecret()),
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Paired, Is.False);
            Assert.That(result.Message, Does.Contain("invalid"));
        }
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
        var local = await _services.Start(Service(localRelay, localStore, localPath));
        await _services.Start(Service(remoteRelay, remoteStore, remotePath, remoteLifetime));

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

    private sealed class LinkedRelay(string host) : NullRelaySender
    {
        private readonly string _host = host;
        private LinkedRelay? _other;
        public override bool IsConnected => _other != null;
        public override event Func<string, MessageKind, ReadOnlyMemory<byte>, Task>? MessageReceived;

        internal static LinkedRelays Create(string leftHost, string rightHost)
        {
            var left = new LinkedRelay(leftHost);
            var right = new LinkedRelay(rightHost);
            left._other = right;
            right._other = left;
            return new LinkedRelays(left, right);
        }

        public override void Send(string[] targetHosts, byte[] payload)
        {
            var other = _other;
            if (other == null || !targetHosts.Contains(other._host, StringComparer.OrdinalIgnoreCase)) return;
            var decoded = MessageSerializer.Decode(payload);
            var handler = other.MessageReceived;
            if (handler != null) _ = handler(_host, decoded.Kind, decoded.Bytes);
        }
    }

    private sealed record LinkedRelays(LinkedRelay Left, LinkedRelay Right);

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

}
