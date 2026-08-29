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

    private RemoteManagementService Service(IRelaySender relay, RemoteManagementStore store, string configPath,
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
}
