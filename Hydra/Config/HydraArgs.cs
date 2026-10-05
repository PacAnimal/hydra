using System.Text;
using Cathedral.Extensions;

namespace Hydra.Config;

/// <summary>
/// The command line shared by the daemon, <c>pair</c> and the TUI: <c>--config X</c> or <c>--config=X</c>,
/// falling back to the <c>CONFIG</c> environment variable, which is how the direct launcher passes it on.
/// Everything else is handed back untouched in <see cref="Rest"/>.
/// </summary>
internal sealed record HydraArgs(string? ExplicitConfigPath, string? EnvironmentConfigPath, IReadOnlyList<string> Rest)
{
    internal const string ConfigVariable = "CONFIG";
    internal const string SessionOption = "--session";
    internal const string ServiceOption = "--service";
    internal const string InstallOption = "--install";
    internal const string UninstallOption = "--uninstall";
    internal const string DemoOption = "--demo";
    internal const string TuiCommand = "tui";
    internal const string PairCommand = "pair";
    internal const string ConfigOption = "--config";

    internal const string DaemonUsage = "Usage: hydra [--config /path/to/hydra.conf] [--install | --uninstall]\n"
        + "       hydra tui [--config /path/to/hydra.conf] [--demo]\n"
        + "       hydra pair [--config /path/to/hydra.conf]";
    internal const string TuiUsage = "Usage: hydra tui [--config /path/to/hydra.conf] [--demo]";
    internal const string PairUsage = "Usage: hydra pair [--config /path/to/hydra.conf]";

    // the Windows service's session child loads the config its service resolved, not its own search's
    internal static string SessionChildArguments(string configPath) => WithConfig(SessionOption, configPath);

    // an option followed by the config it should run with, quoted for a Windows command line
    internal static string WithConfig(string option, string? configPath) =>
        configPath == null ? option : $"{option} {ConfigOption} {QuoteWindowsArgument(configPath)}";

    // what the service control manager runs: the binary in service mode, with the config it was installed for
    internal static string ServiceCommandLine(string executablePath, string? configPath) =>
        $"{QuoteWindowsArgument(executablePath)} {WithConfig(ServiceOption, configPath)}";

    internal string? ConfigPath => ExplicitConfigPath ?? EnvironmentConfigPath;

    // the service or agent runs the config it was installed for; CONFIG only ever came from a launcher
    internal string? InstallConfigPath => ExplicitConfigPath is { } path ? Path.GetFullPath(path) : null;

    // the first argument that is not one of the allowed flags, or null
    internal string? UnknownArgument(params string[] allowed) => Rest.FirstOrDefault(arg => !allowed.Any(arg.EqualsIgnoreCase));

    internal bool Has(string option) => Rest.Any(option.EqualsIgnoreCase);

    /// <summary>
    /// Takes the subcommand out of the command line, so it may stand either side of <c>--config</c>: the
    /// first argument that is not the config option names it, or there is none.
    /// </summary>
    internal static HydraCommandLine SplitCommand(IReadOnlyList<string> args)
    {
        var i = 0;
        while (i < args.Count && (args[i].EqualsIgnoreCase(ConfigOption) || IsConfigAssignment(args[i])))
            i += IsConfigAssignment(args[i]) ? 1 : 2;
        if (i >= args.Count || !(args[i].EqualsIgnoreCase(TuiCommand) || args[i].EqualsIgnoreCase(PairCommand)))
            return new HydraCommandLine(null, [.. args]);
        return new HydraCommandLine(args[i], [.. args.Take(i), .. args.Skip(i + 1)]);
    }

    private static bool IsConfigAssignment(string arg) => arg.StartsWithIgnoreCase(ConfigOption + "=");

    // CommandLineToArgvW's rule: backslashes are literal except in a run that ends at a quote, which is doubled
    internal static string QuoteWindowsArgument(string arg)
    {
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    internal static HydraArgs Parse(IReadOnlyList<string> args) => Parse(args, Environment.GetEnvironmentVariable(ConfigVariable));

    /// <exception cref="ArgumentException"><c>--config</c> without a path, or given twice.</exception>
    internal static HydraArgs Parse(IReadOnlyList<string> args, string? environmentConfig)
    {
        string? configPath = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].EqualsIgnoreCase(ConfigOption))
                configPath = RequireOnce(configPath, i + 1 < args.Count ? args[++i] : null);
            else if (IsConfigAssignment(args[i]))
                configPath = RequireOnce(configPath, args[i][(ConfigOption.Length + 1)..]);
            else
                rest.Add(args[i]);
        }
        return new HydraArgs(configPath, string.IsNullOrWhiteSpace(environmentConfig) ? null : environmentConfig, rest);
    }

    private static string RequireOnce(string? existing, string? value) =>
        existing == null ? RequirePath(value) : throw new ArgumentException($"{ConfigOption} may be given only once.");

    // a following option is a forgotten path, not a file called "--demo"
    private static string RequirePath(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal)
            ? throw new ArgumentException($"{ConfigOption} needs a path, e.g. {ConfigOption} /path/to/hydra.conf")
            : value;
}

internal sealed record HydraCommandLine(string? Command, string[] Arguments);
