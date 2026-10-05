using Hydra.Config;

namespace Hydra.Management;

internal enum GuidedSection { Global, Profile, Relay, Behaviour }

internal enum GuidedFieldKind { Text, Secret, Choice, Integer, Decimal, Toggle, Power }

/// <summary>A JSON object holding some of the form's fields.</summary>
/// <param name="AllOrNothing">Its fields are required members, so the form refuses to write only some of them.</param>
internal sealed record GuidedObject(string Key, string Label, bool AllOrNothing);

/// <summary>
/// One field of the guided config form: where it lives in hydra.conf and where it sits on screen. Global fields are
/// root keys; the rest belong to the profile being edited, optionally inside <see cref="Parent"/>.
/// </summary>
internal sealed record GuidedField(GuidedSection Section, int Row, int Column, GuidedObject? Parent, string Key, GuidedFieldKind Kind, string Label, string Help)
{
    // shown when the key is absent, and so written back on save
    internal string? Fallback { get; init; }
    // shown beside an empty field; an empty field is removed on save
    internal string? DefaultHint { get; init; }
    // the enum a Choice field names; its members are the choices
    internal Type? ChoiceEnum { get; init; }
    internal IReadOnlyList<string> Choices => ChoiceEnum == null ? [] : Enum.GetNames(ChoiceEnum);
    internal bool IsRoot => Section == GuidedSection.Global;
}

internal static class GuidedFields
{
    private static readonly GuidedObject Conditions = new("conditions", "activation conditions", AllOrNothing: false);
    private static readonly GuidedObject EmbeddedStyx = new("embeddedStyx", "embedded relay client", AllOrNothing: true);
    private static readonly GuidedObject EmbeddedStyxServer = new("embeddedStyxServer", "embedded relay server", AllOrNothing: true);

    internal static readonly GuidedField Name = new(GuidedSection.Global, 0, 0, null, "name", GuidedFieldKind.Text, "Machine Name",
        "Name advertised to peers; defaults to the hostname when empty.");
    internal static readonly GuidedField LogLevel = new(GuidedSection.Global, 2, 0, null, "logLevel", GuidedFieldKind.Text, "Log Level",
        "Minimum log detail: trce, dbug, info, warn, fail, or crit.")
    { Fallback = "info" };
    internal static readonly GuidedField ProfileOverride = new(GuidedSection.Global, 4, 0, null, "profile", GuidedFieldKind.Text, "Force Profile",
        "Always select this profile name and ignore its activation conditions. Leave empty for automatic selection.");
    internal static readonly GuidedField AutoUpdate = new(GuidedSection.Global, 6, 0, null, "autoUpdate", GuidedFieldKind.Toggle, "Auto Update",
        "Allow Hydra's built-in updater to check for and apply releases.")
    { Fallback = GuidedValues.On };
    internal static readonly GuidedField DebugShield = new(GuidedSection.Global, 8, 0, null, "debugShield", GuidedFieldKind.Toggle, "Debug Shield",
        "Enable verbose macOS shield diagnostics. Normally leave disabled.")
    { Fallback = GuidedValues.Off };
    internal static readonly GuidedField DebugMouse = new(GuidedSection.Global, 10, 0, null, "debugMouse", GuidedFieldKind.Toggle, "Debug Mouse",
        "Enable verbose mouse routing diagnostics. Normally leave disabled.")
    { Fallback = GuidedValues.Off };

    internal static readonly GuidedField ProfileName = new(GuidedSection.Profile, 5, 0, null, "profileName", GuidedFieldKind.Text, "Profile Name",
        "Display name used by the TUI and optional profile override.");
    internal static readonly GuidedField Mode = new(GuidedSection.Profile, 7, 0, null, "mode", GuidedFieldKind.Choice, "Mode",
        "Master captures and routes input; Slave receives and injects input.")
    { ChoiceEnum = typeof(Mode) };
    internal static readonly GuidedField Ssid = new(GuidedSection.Profile, 9, 0, Conditions, "ssid", GuidedFieldKind.Text, "SSID",
        "Activate this profile only when connected to this Wi-Fi network. Empty means any SSID.")
    { DefaultHint = "any SSID" };
    internal static readonly GuidedField ScreenCount = new(GuidedSection.Profile, 11, 0, Conditions, "screenCount", GuidedFieldKind.Integer, "Screen Count",
        "Activate only when exactly this many local screens are detected. Empty means any count.")
    { DefaultHint = "any count" };
    internal static readonly GuidedField IsPluggedIn = new(GuidedSection.Profile, 5, 1, Conditions, "isPluggedIn", GuidedFieldKind.Power, "Power",
        "Activation condition: any, yes (AC power), or no (battery).")
    { Fallback = "any" };
    internal static readonly GuidedField MouseScale = new(GuidedSection.Profile, 7, 1, null, "mouseScale", GuidedFieldKind.Decimal, "Mouse Scale",
        "Slave fallback cursor-speed multiplier. Master profiles must leave this empty.")
    { DefaultHint = "1.0" };
    internal static readonly GuidedField RelativeMouseScale = new(GuidedSection.Profile, 9, 1, null, "relativeMouseScale", GuidedFieldKind.Decimal, "Relative Scale",
        "Slave fallback relative-mode cursor-speed multiplier.")
    { DefaultHint = "mouse scale" };
    internal static readonly GuidedField DeadCorners = new(GuidedSection.Profile, 11, 1, null, "deadCorners", GuidedFieldKind.Integer, "Dead Corners",
        "Pixels at each screen corner that do not trigger an edge transition.")
    { DefaultHint = "0 px" };
    internal static readonly GuidedField MaxMouseHz = new(GuidedSection.Profile, 13, 1, null, "maxMouseHz", GuidedFieldKind.Integer, "Max Mouse Hz",
        "Master: how many mouse updates a second are sent to a slave. Higher costs master CPU.")
    { DefaultHint = $"{HydraProfile.DefaultMaxMouseHz} Hz" };
    internal static readonly GuidedField ClipboardSync = new(GuidedSection.Profile, 13, 0, null, "clipboardSync", GuidedFieldKind.Choice, "Clipboard",
        "Hydra syncs clipboards itself. System lets a macOS master leave Mac peers to Universal Clipboard.")
    {
        DefaultHint = nameof(ClipboardSyncMode.Hydra),
        ChoiceEnum = typeof(ClipboardSyncMode)
    };

    internal static readonly GuidedField NetworkConfig = new(GuidedSection.Relay, 0, 0, null, "networkConfig", GuidedFieldKind.Secret, "Network Config",
        "Encrypted/base64 Styx network configuration shared by peers.");
    internal static readonly GuidedField EmbeddedServer = new(GuidedSection.Relay, 3, 0, EmbeddedStyx, "server", GuidedFieldKind.Text, "Embedded URL",
        "Connect to an embedded Styx relay at this URL instead of using networkConfig.");
    internal static readonly GuidedField EmbeddedPassword = new(GuidedSection.Relay, 5, 0, EmbeddedStyx, "password", GuidedFieldKind.Secret, "Password",
        "Password for the embedded Styx relay URL above. Masked while typing.");
    internal static readonly GuidedField EmbeddedPort = new(GuidedSection.Relay, 8, 0, EmbeddedStyxServer, "port", GuidedFieldKind.Integer, "Local Port",
        "Run an embedded Styx relay on this TCP port.");
    internal static readonly GuidedField EmbeddedServerPassword = new(GuidedSection.Relay, 10, 0, EmbeddedStyxServer, "password", GuidedFieldKind.Secret, "Password",
        "Password used by peers connecting to this machine's embedded relay. Masked while typing.");

    internal static readonly GuidedField HideCursor = new(GuidedSection.Behaviour, 0, 0, null, "hideCursor", GuidedFieldKind.Toggle, "Hide Cursor",
        "Hide the master's local cursor after inactivity. Master only.")
    { Fallback = GuidedValues.Off };
    internal static readonly GuidedField RemoteOnly = new(GuidedSection.Behaviour, 2, 0, null, "remoteOnly", GuidedFieldKind.Toggle, "Remote Only",
        "Treat this master as a headless input forwarder with no local screen route.")
    { Fallback = GuidedValues.Off };
    internal static readonly GuidedField SyncScreensaver = new(GuidedSection.Behaviour, 4, 0, null, "syncScreensaver", GuidedFieldKind.Toggle, "Sync Screensaver",
        "Synchronize screensaver activation with connected peers.")
    { Fallback = GuidedValues.On };
    internal static readonly GuidedField ScreenLockPropagation = new(GuidedSection.Behaviour, 6, 0, null, "screenLockPropagation", GuidedFieldKind.Toggle,
        "Propagate Screen Lock", "Propagate this master's machine lock to connected slaves.")
    { Fallback = GuidedValues.Off };
    internal static readonly GuidedField AccelerateMouseWheel = new(GuidedSection.Behaviour, 8, 0, null, "accelerateMouseWheel", GuidedFieldKind.Toggle,
        "Accelerate Wheel", "Apply Hydra's scroll-wheel acceleration behavior.")
    { Fallback = GuidedValues.On };
    internal static readonly GuidedField UnicodeKeyRepeat = new(GuidedSection.Behaviour, 10, 0, null, "unicodeKeyRepeat", GuidedFieldKind.Toggle,
        "Unicode Key Repeat", "Repeat printable keys as Unicode on Mac slaves to avoid the accent popup.")
    { Fallback = GuidedValues.On };
    internal static readonly GuidedField AllowSystemSleep = new(GuidedSection.Behaviour, 12, 0, null, "allowSystemSleep", GuidedFieldKind.Toggle,
        "Allow System Sleep", "Let the OS sleep this machine on its normal idle policy; Hydra leaves the relay before it suspends.")
    { Fallback = GuidedValues.Off };
    internal static readonly IReadOnlyList<GuidedField> All =
    [
        Name, LogLevel, ProfileOverride, AutoUpdate, DebugShield, DebugMouse,
        ProfileName, Mode, Ssid, ScreenCount, ClipboardSync, IsPluggedIn, MouseScale, RelativeMouseScale, DeadCorners, MaxMouseHz,
        NetworkConfig, EmbeddedServer, EmbeddedPassword, EmbeddedPort, EmbeddedServerPassword,
        HideCursor, RemoteOnly, SyncScreensaver, ScreenLockPropagation, AccelerateMouseWheel, UnicodeKeyRepeat, AllowSystemSleep
    ];

    internal static readonly IReadOnlyList<GuidedField> Root = [.. All.Where(f => f.IsRoot)];
    internal static readonly IReadOnlyList<GuidedField> Profile = [.. All.Where(f => !f.IsRoot)];
}
