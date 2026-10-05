using System.Reflection;

namespace Hydra.Update;

/// <summary>
/// The release version, e.g. <c>0.1.42</c>: what the TUI shows and what <see cref="SelfUpdater"/> compares
/// with a release tag. Read from this assembly, never the entry assembly, which under a test host is the
/// runner. The informational version is the one <c>-p:Version</c> lands in verbatim.
/// </summary>
internal static class HydraVersion
{
    internal static string Current { get; } =
        Release(typeof(HydraVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    // drops the "+commit" build metadata the SDK appends
    internal static string Release(string? informationalVersion) => informationalVersion?.Split('+')[0] ?? "0.0.0";
}
