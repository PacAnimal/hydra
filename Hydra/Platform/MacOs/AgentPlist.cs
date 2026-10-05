using System.Security;
using System.Xml.Linq;
using Cathedral.Extensions;
using Hydra.Config;

namespace Hydra.Platform.MacOs;

/// <summary>
/// The LaunchAgent's plist, written by <c>--install</c> and read back by the TUI's Start. Plain XML, so it
/// is built and tested on every platform even though only macOS runs it.
/// </summary>
internal static class AgentPlist
{
    internal const string Label = "com.cathedral.hydra";

    internal static string Generate(string exePath, string workingDir, string logDir, string? configPath)
    {
        var exe = SecurityElement.Escape(exePath);
        var wd = SecurityElement.Escape(workingDir);
        var stdout = SecurityElement.Escape(Path.Combine(logDir, "hydra.stdout.log"));
        var stderr = SecurityElement.Escape(Path.Combine(logDir, "hydra.stderr.log"));
        var configArguments = configPath == null
            ? ""
            : $"\n        <string>{HydraArgs.ConfigOption}</string>\n        <string>{SecurityElement.Escape(configPath)}</string>";

        // ProcessType and Nice are the whole reason this agent can keep up on a loaded machine. Without
        // ProcessType launchd classifies us as a background job and throttles our CPU and I/O; Nice is
        // privileged and launchd is the only one in a position to apply it on our behalf — the process
        // itself runs as the user and cannot. ProcessPriority.Raise() then only matches what we hold.
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{Label}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{exe}</string>{configArguments}
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <true/>
                <key>StandardOutPath</key>
                <string>{stdout}</string>
                <key>StandardErrorPath</key>
                <string>{stderr}</string>
                <key>WorkingDirectory</key>
                <string>{wd}</string>
                <key>ThrottleInterval</key>
                <integer>5</integer>
                <key>ProcessType</key>
                <string>Interactive</string>
                <key>Nice</key>
                <integer>{ProcessPriority.UnixNice}</integer>
            </dict>
            </plist>
            """;
    }

    // the config the installed agent runs: the one it was installed with, else the default beside its binary
    internal static string ConfigPathOf(string plist)
    {
        var dict = XDocument.Parse(plist).Root?.Element("dict") ?? throw new InvalidOperationException("The agent's plist has no dict.");
        var arguments = ValueOf(dict, "ProgramArguments")?.Elements("string").Select(e => e.Value).ToList() ?? [];
        var config = arguments.FindIndex(a => a.EqualsIgnoreCase(HydraArgs.ConfigOption));
        if (config >= 0 && config + 1 < arguments.Count) return arguments[config + 1];
        var workingDir = ValueOf(dict, "WorkingDirectory")?.Value ?? throw new InvalidOperationException("The agent's plist has no working directory.");
        return Path.Combine(workingDir, "hydra.conf");
    }

    private static XElement? ValueOf(XElement dict, string key) =>
        dict.Elements("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().FirstOrDefault();
}
