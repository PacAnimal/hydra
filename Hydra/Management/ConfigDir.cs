namespace Hydra.Management;

/// <summary>The files Hydra keeps beside <c>hydra.conf</c>.</summary>
internal sealed class ConfigDir(string configPath)
{
    internal string DirectoryPath { get; } = Path.GetDirectoryName(Path.GetFullPath(configPath))!;

    /// <summary>
    /// The lock guarding <c>hydra.conf</c> and everything that rewrites it — the TUI's save, a remote apply
    /// and its rollback. ONE lock for all of them, because they contend over the same file and a lock per
    /// store would let two of them replace it at once.
    /// </summary>
    internal string ConfigLock => Named(".hydra-config.lock");

    /// <summary>The lock guarding <see cref="ManagementState"/>.</summary>
    internal string ManagementLock => Named(".hydra-management.lock");

    // pairing codes and controller/target secrets
    internal string ManagementState => Named(".hydra-management.json");

    // an unconfirmed remote apply and the config it replaced
    internal string RemoteApplyMarker => Named(".hydra-remote-apply.json");
    internal string RemoteApplyBackup => Named(".hydra-remote-backup.conf");

    private string Named(string name) => Path.Combine(DirectoryPath, name);
}
