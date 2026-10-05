using System.Reflection;
using Hydra.Management;

namespace Tests.Management;

/// <summary>
/// A versioned TUI and daemon, and two paired daemons, talk over these strings, so renaming one is a protocol
/// change. Every const is listed, so a new one fails here until it is pinned too.
/// </summary>
[TestFixture]
public class ManagementWireNamesTests
{
    [Test]
    public void ManagementMethods_KeepTheirWireNames() =>
        Assert.That(Consts(typeof(ManagementMethods)), Is.EquivalentTo(new Dictionary<string, string>
        {
            [nameof(ManagementMethods.Hello)] = "hello",
            [nameof(ManagementMethods.Status)] = "status",
            [nameof(ManagementMethods.Logs)] = "logs",
            [nameof(ManagementMethods.ConfigGet)] = "config.get",
            [nameof(ManagementMethods.ConfigValidate)] = "config.validate",
            [nameof(ManagementMethods.ConfigSave)] = "config.save",
            [nameof(ManagementMethods.RelayReconnect)] = "relay.reconnect",
            [nameof(ManagementMethods.HydraRestart)] = "hydra.restart",
            [nameof(ManagementMethods.HydraShutdown)] = "hydra.shutdown",
            [nameof(ManagementMethods.RemotePair)] = "remote.pair",
            [nameof(ManagementMethods.RemoteConfigGet)] = "remote.config.get",
            [nameof(ManagementMethods.RemoteConfigValidate)] = "remote.config.validate",
            [nameof(ManagementMethods.RemoteConfigApply)] = "remote.config.apply",
            [nameof(ManagementMethods.RemoteConfigConfirm)] = "remote.config.confirm",
        }));

    [Test]
    public void RemoteOperations_KeepTheirWireNames() =>
        Assert.That(Consts(typeof(RemoteOperations)), Is.EquivalentTo(new Dictionary<string, string>
        {
            [nameof(RemoteOperations.Pair)] = "pair",
            [nameof(RemoteOperations.ConfigGet)] = "config.get",
            [nameof(RemoteOperations.ConfigValidate)] = "config.validate",
            [nameof(RemoteOperations.ConfigApply)] = "config.apply",
            [nameof(RemoteOperations.ConfigConfirm)] = "config.confirm",
        }));

    private static Dictionary<string, string> Consts(Type type) =>
        type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.IsLiteral)
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);
}
