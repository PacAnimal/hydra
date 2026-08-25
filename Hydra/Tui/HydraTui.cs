using System.Text;
using System.Net.Sockets;
using Hydra.Config;
using Hydra.Management;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Hydra;

internal static class HydraTui
{
    internal static bool HasRestarted(HydraStatusSnapshot previous, HydraStatusSnapshot current) =>
        current.ProcessId != previous.ProcessId || current.UptimeSeconds + 1 < previous.UptimeSeconds;

    internal static Task RunAsync(string[] args)
    {
        string? explicitConfig = null;
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--config" && i + 1 < args.Length)
                explicitConfig = args[++i];

        string configPath;
        try
        {
            configPath = HydraConfigFile.ResolvePath(explicitConfig ?? Environment.GetEnvironmentVariable("CONFIG"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Task.CompletedTask;
        }

        using IApplication app = Application.Create();
        app.Init();
        Button.DefaultShadow = ShadowStyles.None;
        using var window = new Window { Title = "Hydra Control Center", BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded };
        using var controller = new TuiController(app, window, configPath);
        controller.Build();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            app.Invoke(window.RequestStop);
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            app.Run(window);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
        return Task.CompletedTask;
    }

    private sealed class TuiController(IApplication app, Window window, string configPath) : IDisposable
    {
        private readonly ManagementClient _client = new(configPath);
        private readonly TransactionalConfigStore _offlineStore = new(new HydraRuntimeInfo(configPath, DateTimeOffset.UtcNow));
        private readonly CancellationTokenSource _cancel = new();
        private readonly Editor _overview = ReadOnlyEditor();
        private readonly Editor _peers = ReadOnlyEditor();
        private readonly Editor _logs = ReadOnlyEditor();
        private readonly Editor _config = new() { WordWrap = false, ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar | ViewportSettingsFlags.HasHorizontalScrollBar };
        private readonly FrameView _configForm = new() { BorderStyle = Terminal.Gui.Drawing.LineStyle.None };
        private readonly FrameView _configText = new() { BorderStyle = Terminal.Gui.Drawing.LineStyle.None, Visible = false };
        private readonly Label _configHelp = new() { Text = "Move focus or hover over an option to see what it does.", X = 1, Y = 0, Width = Dim.Fill(1), Height = 2 };
        private readonly Button _formModeButton = new() { Text = "_Form" };
        private readonly Button _textModeButton = new() { Text = "_Text" };
        private readonly Button _previousProfile = new() { Text = "_Previous", Enabled = false };
        private readonly Button _nextProfile = new() { Text = "_Next", Enabled = false };
        private readonly Button _revealSecrets = new() { Text = "_Reveal Secrets", Visible = false };
        private readonly Editor _diagnostics = ReadOnlyEditor();
        private readonly Label _connection = new() { Text = "Connecting…", X = 1, Y = 0, Width = Dim.Fill(), SchemeName = "Accent" };
        private readonly Label _activity = new() { Text = "Ready", X = 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), SchemeName = "Base" };
        private readonly Button _reconnect = new() { Text = "_Reconnect Relay", Enabled = false };
        private readonly Button _restart = new() { Text = "_Restart Hydra", Enabled = false };
        private readonly Queue<string> _visibleLogs = new();
        private ConfigDocument? _configDocument;
        private string? _configWithSecrets;
        private HydraStatusSnapshot? _lastStatus;
        private long _logCursor;
        private int _refreshing;
        private bool _connected;
        private bool _liveControlsReady;
        private bool _helloComplete;
        private bool _configMaskFailed;
        private bool _secretsRevealed;
        private bool _guidedMode = true;
        private bool _commandBusy;
        private GuidedConfigDocument? _guidedConfig;
        private int _guidedProfileIndex;
        private readonly List<(View Button, FrameView Content, string Name)> _tabs = [];
        private readonly List<(Button Button, FrameView Content, string Name)> _formSections = [];
        private int _activeTab;

        private readonly TextField _rootName = new();
        private readonly TextField _rootLogLevel = new();
        private readonly TextField _rootProfileOverride = new();
        private readonly CheckBox _rootAutoUpdate = new() { Text = "Auto Update" };
        private readonly CheckBox _rootDebugShield = new() { Text = "Debug Shield" };
        private readonly CheckBox _rootDebugMouse = new() { Text = "Debug Mouse" };
        private readonly Label _profilePosition = new();
        private readonly TextField _profileName = new();
        private readonly TextField _profileMode = new();
        private readonly TextField _conditionSsid = new();
        private readonly TextField _conditionScreens = new();
        private readonly TextField _conditionPower = new();
        private readonly TextField _networkConfig = new() { Secret = true };
        private readonly TextField _embeddedServer = new();
        private readonly TextField _embeddedPassword = new() { Secret = true };
        private readonly TextField _embeddedPort = new();
        private readonly TextField _embeddedServerPassword = new() { Secret = true };
        private readonly TextField _mouseScale = new();
        private readonly TextField _relativeMouseScale = new();
        private readonly TextField _deadCorners = new();
        private readonly CheckBox _hideCursor = new() { Text = "Hide Cursor" };
        private readonly CheckBox _remoteOnly = new() { Text = "Remote Only" };
        private readonly CheckBox _syncScreensaver = new() { Text = "Sync Screensaver" };
        private readonly CheckBox _screenLockPropagation = new() { Text = "Propagate Screen Lock" };
        private readonly CheckBox _accelerateMouseWheel = new() { Text = "Accelerate Wheel" };
        private readonly CheckBox _unicodeKeyRepeat = new() { Text = "Unicode Key Repeat" };
        private readonly Label _advancedSummary = new();

        internal void Build()
        {
            window.Add(_connection, _activity);
            var navigation = new View { X = 1, Y = 2, Width = Dim.Fill(), Height = 2 };
            var contents = new[]
            {
                ("Overview", BuildOverviewTab()),
                ("Peers & Screens", BuildTextTab("Peers & Screens", _peers)),
                ("Logs", BuildTextTab("Logs", _logs)),
                ("Configuration", BuildConfigTab()),
                ("Diagnostics", BuildTextTab("Diagnostics", _diagnostics)),
                ("Help", BuildHelpTab())
            };
            View? previous = null;
            for (var index = 0; index < contents.Length; index++)
            {
                var button = new View
                {
                    Title = $"_{contents[index].Item1}",
                    X = previous == null ? 0 : Pos.Right(previous),
                    Y = 0,
                    Width = contents[index].Item1.Length + 4,
                    Height = 2,
                    BorderStyle = Terminal.Gui.Drawing.LineStyle.Rounded,
                    CanFocus = false,
                    MouseHighlightStates = MouseState.None
                };
                navigation.Add(button);
                var content = contents[index].Item2;
                content.X = 0;
                content.Y = 4;
                content.Width = Dim.Fill();
                content.Height = Dim.Fill(4);
                content.Visible = false;
                window.Add(content);
                _tabs.Add((button, content, contents[index].Item1));
                previous = button;
            }
            window.Add(navigation);
            app.Mouse.MouseEvent += HandleMainTabMouse;
            SelectTab(0);

            var status = new StatusBar([
                new Shortcut(Application.GetDefaultKey(Command.Quit), "Quit", () => window.RequestStop()),
                new Shortcut(Key.F5, "Refresh", () => _ = RefreshAsync()),
                new Shortcut(Key.F1, "Help", () => SelectTab(_tabs.Count - 1))
            ]);
            window.Add(status);

            app.AddTimeout(TimeSpan.FromSeconds(2), () =>
            {
                _ = RefreshAsync();
                return true;
            });
            _ = LoadConfigAsync();
            _ = RefreshAsync();
        }

        private void SelectTab(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;
            _activeTab = index;
            for (var i = 0; i < _tabs.Count; i++)
            {
                var selected = i == index;
                _tabs[i].Content.Visible = selected;
                _tabs[i].Button.SchemeName = selected ? "Accent" : "Base";
            }
        }

        private void HandleMainTabMouse(object? sender, Terminal.Gui.Input.Mouse mouse)
        {
            if (!mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed)) return;
            for (var index = 0; index < _tabs.Count; index++)
            {
                if (!_tabs[index].Button.FrameToScreen().Contains(mouse.ScreenPosition)) continue;
                mouse.Handled = true;
                SelectTab(index);
                return;
            }
        }

        private FrameView BuildOverviewTab()
        {
            var tab = BuildTextTab("_Overview", _overview);
            _overview.Height = Dim.Fill(3);
            _reconnect.X = 1;
            _reconnect.Y = Pos.AnchorEnd(2);
            _reconnect.Accepting += (_, e) =>
            {
                e.Handled = true;
                _ = RunCommandAsync(() => _client.ReconnectRelayAsync(_cancel.Token), CommandKind.ReconnectRelay);
            };
            _restart.X = Pos.Right(_reconnect) + 2;
            _restart.Y = Pos.Top(_reconnect);
            _restart.Accepting += (_, e) =>
            {
                e.Handled = true;
                if (MessageBox.Query(app, "Restart Hydra", "Restart the running Hydra process?", "Restart", "Cancel") == 0)
                    _ = RunCommandAsync(() => _client.RestartHydraAsync(_cancel.Token), CommandKind.RestartHydra);
            };
            tab.Add(_reconnect, _restart);
            return tab;
        }

        private FrameView BuildConfigTab()
        {
            var tab = new FrameView { Title = "Configuration", Width = Dim.Fill(), Height = Dim.Fill() };
            _formModeButton.X = 1; _formModeButton.Y = 0;
            _textModeButton.X = Pos.Right(_formModeButton) + 1; _textModeButton.Y = 0;
            _formModeButton.CanFocus = true;
            _formModeButton.MouseHighlightStates = MouseState.None;
            _textModeButton.CanFocus = true;
            _textModeButton.MouseHighlightStates = MouseState.None;
            _formModeButton.Accepting += (_, e) => { e.Handled = true; SwitchConfigMode(guided: true); };
            _textModeButton.Accepting += (_, e) => { e.Handled = true; SwitchConfigMode(guided: false); };

            _configForm.X = _configText.X = 0;
            _configForm.Y = _configText.Y = 2;
            _configForm.Width = _configText.Width = Dim.Fill();
            _configForm.Height = _configText.Height = Dim.Fill(7);
            BuildGuidedConfigForm();

            _config.X = 0;
            _config.Y = 0;
            _config.Width = Dim.Fill();
            _config.Height = Dim.Fill();

            var helpPanel = new FrameView { Title = "Option Help", X = 0, Y = Pos.AnchorEnd(6), Width = Dim.Fill(), Height = 3 };
            helpPanel.Add(_configHelp);
            var validate = new Button { Text = "_Validate", X = 1, Y = Pos.AnchorEnd(2) };
            validate.Accepting += (_, e) => { e.Handled = true; ValidateConfig(); };
            var reload = new Button { Text = "Re_load", X = Pos.Right(validate) + 2, Y = Pos.Top(validate) };
            reload.Accepting += (_, e) => { e.Handled = true; _ = LoadConfigAsync(); };
            _revealSecrets.X = Pos.Right(reload) + 2; _revealSecrets.Y = Pos.Top(validate);
            _revealSecrets.Accepting += (_, e) =>
            {
                e.Handled = true;
                ToggleSecrets(_revealSecrets);
            };
            var save = new Button { Text = "_Save", X = Pos.Right(_revealSecrets) + 2, Y = Pos.Top(validate) };
            save.Accepting += (_, e) => { e.Handled = true; _ = SaveConfigAsync(restart: false); };
            var apply = new Button { Text = "Save && _Restart", X = Pos.Right(save) + 2, Y = Pos.Top(validate) };
            apply.Accepting += (_, e) => { e.Handled = true; _ = SaveConfigAsync(restart: true); };
            _configText.Add(_config);
            tab.Add(_formModeButton, _textModeButton, _configForm, _configText, helpPanel,
                validate, reload, _revealSecrets, save, apply);

            BindConfigHelp(_formModeButton, "Form Mode", "Edit common Hydra settings in labelled fields. Advanced topology remains unchanged.");
            BindConfigHelp(_textModeButton, "Text Mode", "Edit the complete hydra.conf JSON, including hosts, neighbours and screen definitions.");
            BindConfigHelp(_config, "Raw Configuration", "Complete hydra.conf JSON. Secrets stay masked until Reveal Secrets is selected.");
            BindConfigHelp(validate, "Validate", "Check the current form or JSON with Hydra's canonical parser without writing the file.");
            BindConfigHelp(reload, "Reload", "Discard unsaved edits and reload hydra.conf from disk or the running daemon.");
            BindConfigHelp(_revealSecrets, "Reveal Secrets", "Temporarily show relay credentials in Text mode. Avoid this in recorded or shared terminals.");
            BindConfigHelp(save, "Save", "Validate and atomically replace hydra.conf. The running Hydra process is not restarted.");
            BindConfigHelp(apply, "Save and Restart", "Validate, atomically save, then restart Hydra so the new configuration becomes active.");
            ShowConfigMode(guided: true);
            return tab;
        }

        private void BuildGuidedConfigForm()
        {
            var global = new FrameView { Title = "Global", X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill() };
            AddField(global, "Machine Name", _rootName, 0, 26, "Name advertised to peers; defaults to the hostname when empty.");
            AddField(global, "Log Level", _rootLogLevel, 2, 26, "Minimum log detail: trce, dbug, info, warn, fail, or crit.");
            AddField(global, "Force Profile", _rootProfileOverride, 4, 26, "Always select this profile name and ignore its activation conditions. Leave empty for automatic selection.");
            PlaceCheckBox(global, _rootAutoUpdate, 6, "Allow Hydra's built-in updater to check for and apply releases.");
            PlaceCheckBox(global, _rootDebugShield, 8, "Enable verbose macOS shield diagnostics. Normally leave disabled.");
            PlaceCheckBox(global, _rootDebugMouse, 10, "Enable verbose mouse routing diagnostics. Normally leave disabled.");

            var profile = new FrameView { Title = "Profile", X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(), Visible = false };
            _profilePosition.X = 1; _profilePosition.Y = 0; _profilePosition.Width = 66;
            _previousProfile.X = 1; _previousProfile.Y = 2;
            _nextProfile.X = Pos.Right(_previousProfile) + 2; _nextProfile.Y = 2;
            _previousProfile.Accepting += (_, e) => { e.Handled = true; ChangeGuidedProfile(-1); };
            _nextProfile.Accepting += (_, e) => { e.Handled = true; ChangeGuidedProfile(1); };
            AddFieldAt(profile, "Profile Name", _profileName, 1, 15, 5, 18, "Display name used by the TUI and optional profile override.");
            AddFieldAt(profile, "Mode", _profileMode, 1, 15, 7, 18, "Master captures and routes input; Slave receives and injects input.");
            AddFieldAt(profile, "SSID", _conditionSsid, 1, 15, 9, 18, "Activate this profile only when connected to this Wi-Fi network. Empty means any SSID.");
            AddFieldAt(profile, "Screen Count", _conditionScreens, 1, 15, 11, 18, "Activate only when exactly this many local screens are detected. Empty means any count.");
            AddFieldAt(profile, "Power", _conditionPower, 53, 69, 5, 18, "Activation condition: any, yes (AC power), or no (battery).");
            AddFieldAt(profile, "Mouse Scale", _mouseScale, 53, 69, 7, 18, "Slave fallback cursor-speed multiplier. Master profiles must leave this empty.");
            AddFieldAt(profile, "Relative Scale", _relativeMouseScale, 53, 69, 9, 18, "Slave fallback relative-mode cursor-speed multiplier.");
            AddFieldAt(profile, "Dead Corners", _deadCorners, 53, 69, 11, 18, "Pixels at each screen corner that do not trigger an edge transition.");
            AddDefaultHint(profile, _conditionSsid, "any SSID");
            AddDefaultHint(profile, _conditionScreens, "any count");
            AddDefaultHint(profile, _mouseScale, "1.0");
            AddDefaultHint(profile, _relativeMouseScale, "mouse scale");
            AddDefaultHint(profile, _deadCorners, "0 px");
            profile.Add(_profilePosition, _previousProfile, _nextProfile);
            BindConfigHelp(_previousProfile, "Previous Profile", "Move to the previous profile. Disabled on the first profile or when only one exists.");
            BindConfigHelp(_nextProfile, "Next Profile", "Move to the next profile. Disabled on the last profile or when only one exists.");

            var relay = new FrameView { Title = "Relay", X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(), Visible = false };
            AddField(relay, "Network Config", _networkConfig, 0, 38, "Encrypted/base64 Styx network configuration shared by peers.");
            AddField(relay, "Embedded URL", _embeddedServer, 3, 38, "Connect to an embedded Styx relay at this URL instead of using networkConfig.");
            AddField(relay, "Password", _embeddedPassword, 5, 38, "Password for the embedded Styx relay URL above. Masked while typing.");
            AddField(relay, "Local Port", _embeddedPort, 8, 38, "Run an embedded Styx relay on this TCP port.");
            AddField(relay, "Password", _embeddedServerPassword, 10, 38, "Password used by peers connecting to this machine's embedded relay. Masked while typing.");
            relay.Add(new Label { Text = "Secrets remain masked in Form mode.", X = 1, Y = 11 });

            var behavior = new FrameView { Title = "Behaviour & Topology", X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(), Visible = false };
            PlaceCheckBox(behavior, _hideCursor, 0, "Hide the master's local cursor after inactivity. Master only.");
            PlaceCheckBox(behavior, _remoteOnly, 2, "Treat this master as a headless input forwarder with no local screen route.");
            PlaceCheckBox(behavior, _syncScreensaver, 4, "Synchronize screensaver activation with connected peers.");
            PlaceCheckBox(behavior, _screenLockPropagation, 6, "Propagate this master's machine lock to connected slaves.");
            PlaceCheckBox(behavior, _accelerateMouseWheel, 8, "Apply Hydra's scroll-wheel acceleration behavior.");
            PlaceCheckBox(behavior, _unicodeKeyRepeat, 10, "Repeat printable keys as Unicode on Mac slaves to avoid the accent popup.");

            _advancedSummary.X = 1; _advancedSummary.Y = 12; _advancedSummary.Width = Dim.Fill(1); _advancedSummary.Height = 3;
            behavior.Add(_advancedSummary);
            BindConfigHelp(_advancedSummary, "Advanced Topology", "Host neighbours and per-screen matching are preserved here and editable in Text mode.");

            var sections = new[]
            {
                ("Global", global),
                ("Profile", profile),
                ("Relay", relay),
                ("Behaviour", behavior)
            };
            Button? previous = null;
            for (var index = 0; index < sections.Length; index++)
            {
                var captured = index;
                var button = new Button
                {
                    Text = $"_{sections[index].Item1}",
                    X = previous == null ? 1 : Pos.Right(previous) + 1,
                    Y = 0,
                    CanFocus = true,
                    MouseHighlightStates = MouseState.None
                };
                button.Accepting += (_, e) => { e.Handled = true; SelectFormSection(captured); };
                _configForm.Add(button, sections[index].Item2);
                _formSections.Add((button, sections[index].Item2, sections[index].Item1));
                previous = button;
            }
            SelectFormSection(0);
        }

        private void SelectFormSection(int index)
        {
            if (index < 0 || index >= _formSections.Count) return;
            for (var i = 0; i < _formSections.Count; i++)
            {
                var selected = i == index;
                _formSections[i].Content.Visible = selected;
                _formSections[i].Button.SchemeName = selected ? "Accent" : "Base";
            }
            _formSections[index].Button.SetFocus();
        }

        private static void AddDefaultHint(View parent, TextField field, string defaultValue)
        {
            var hint = new Label
            {
                Text = $"default: {defaultValue}",
                X = Pos.Right(field) + 1,
                Y = Pos.Top(field),
                SchemeName = "Accent",
                Visible = string.IsNullOrWhiteSpace(field.Text)
            };
            field.TextChanged += (_, _) => hint.Visible = string.IsNullOrWhiteSpace(field.Text);
            parent.Add(hint);
        }

        private void AddField(View parent, string label, TextField field, int y, int width, string help)
        {
            AddFieldAt(parent, label, field, 1, 18, y, width, help);
        }

        private void AddFieldAt(View parent, string label, TextField field, int labelX, int fieldX, int y, int width, string help)
        {
            var caption = new Label { Text = label, X = labelX, Y = y, Width = fieldX - labelX - 1 };
            field.X = fieldX;
            field.Y = y;
            field.Width = width;
            parent.Add(caption, field);
            BindConfigHelp(caption, label, help);
            BindConfigHelp(field, label, help);
        }

        private void PlaceCheckBox(View parent, CheckBox box, int y, string help)
        {
            box.X = 1;
            box.Y = y;
            parent.Add(box);
            BindConfigHelp(box, box.Text, help);
        }

        private void BindConfigHelp(View view, string title, string help)
        {
            void Show() => _configHelp.Text = $"{title}: {help}";
            view.MouseEnter += (_, _) => Show();
            view.HasFocusChanged += (_, e) => { if (e.CurrentValue) Show(); };
        }

        private static FrameView BuildTextTab(string title, Editor editor)
        {
            var tab = new FrameView { Title = title, Width = Dim.Fill(), Height = Dim.Fill() };
            editor.X = 0;
            editor.Y = 0;
            editor.Width = Dim.Fill();
            editor.Height = Dim.Fill();
            tab.Add(editor);
            return tab;
        }

        private static FrameView BuildHelpTab()
        {
            var help = ReadOnlyEditor();
            help.Text = """
                Hydra Control Center

                F1        Open this help tab
                F5        Refresh now
                Tab       Move between controls
                Esc       Quit the TUI (Hydra keeps running)

                Overview shows the live local Hydra process, active profile, relay, selected network
                interface/socket, traffic counters and routing state.
                Peers & Screens lists state already known by this Hydra instance.
                Logs are a bounded local monitoring stream and never include clipboard/file-transfer content.
                Configuration has a Form mode for common settings and Text mode for complete JSON.
                Switching modes preserves advanced fields. Validate before saving. Save & restart atomically
                writes the file and asks the daemon to restart; external edits are detected.

                When the daemon is offline, configuration remains available but live controls are disabled.
                """;
            return BuildTextTab("_Help", help);
        }

        private async Task RefreshAsync()
        {
            if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
                if (!_helloComplete)
                {
                    var hello = await _client.HelloAsync(timeout.Token);
                    if (hello.ProtocolVersion != ManagementProtocol.Version)
                        throw new InvalidOperationException($"Management protocol {hello.ProtocolVersion} is incompatible with this TUI ({ManagementProtocol.Version}).");
                    _helloComplete = true;
                }
                var status = await _client.GetStatusAsync(timeout.Token);
                var logs = await _client.GetLogsAsync(_logCursor, timeout.Token);
                _connected = true;
                _lastStatus = status;
                _logCursor = logs.LatestCursor;
                app.Invoke(() => Render(status, logs));
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
            {
                _connected = false;
                _helloComplete = false;
                _liveControlsReady = false;
            }
            catch (Exception ex)
            {
                _connected = false;
                _helloComplete = false;
                app.Invoke(() =>
                {
                    SetLiveControls(false);
                    _connection.Text = "○ Management unavailable — Hydra may still be running; configuration editing remains available";
                    _diagnostics.Text = FormatDiagnostics(ex);
                });
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        }

        private void Render(HydraStatusSnapshot status, ManagementLogPage page)
        {
            SetLiveControls(true);
            SetText(_connection, $"● Connected  │  Hydra {status.Version}  │  {status.HostName}  │  {status.ProfileName ?? "idle"} / {status.Mode}");
            SetText(_overview, FormatOverview(status));
            SetText(_peers, FormatPeers(status));
            if (page.Entries.Count > 0)
            {
                foreach (var entry in page.Entries)
                {
                    _visibleLogs.Enqueue($"{entry.Timestamp.ToLocalTime():HH:mm:ss} {entry.Level,-11} {ShortCategory(entry.Category),-24} {entry.Message}");
                    while (_visibleLogs.Count > ManagementLogBuffer.Capacity)
                        _visibleLogs.Dequeue();
                }
                SetText(_logs, string.Join('\n', _visibleLogs));
            }
            SetText(_diagnostics, FormatDiagnostics());
        }

        private static void SetText(View view, string value)
        {
            if (view.Text != value) view.Text = value;
        }

        private async Task LoadConfigAsync()
        {
            try
            {
                ConfigDocument document;
                try
                {
                    document = await _client.GetConfigAsync(_cancel.Token);
                    _connected = true;
                }
                catch (Exception ex) when (ex is IOException or SocketException)
                {
                    document = await _offlineStore.ReadAsync(_cancel.Token);
                }
                _configDocument = document;
                _configWithSecrets = document.Json;
                _secretsRevealed = false;
                try
                {
                    var masked = ConfigSecretMask.Mask(document.Json);
                    _configMaskFailed = false;
                    app.Invoke(() =>
                    {
                        _config.Text = masked;
                        try
                        {
                            LoadGuidedConfig(document.Json);
                        }
                        catch (Exception)
                        {
                            ShowConfigMode(guided: false);
                        }
                    });
                }
                catch (System.Text.Json.JsonException)
                {
                    _configMaskFailed = true;
                    app.Invoke(() =>
                    {
                        _config.Text = "Configuration JSON is invalid and cannot be safely masked.\nUse Reveal secrets to inspect and repair the raw document.";
                        ShowConfigMode(guided: false);
                    });
                }
            }
            catch (Exception ex)
            {
                app.Invoke(() => MessageBox.ErrorQuery(app, "Configuration", ex.Message, "OK"));
            }
        }

        private void ValidateConfig()
        {
            try
            {
                var result = TransactionalConfigStore.Validate(CurrentConfigForSave());
                if (result.Valid)
                    MessageBox.Query(app, "Configuration", "Configuration is valid.", "OK");
                else
                    MessageBox.ErrorQuery(app, "Configuration Error", result.Error ?? "Invalid configuration.", "OK");
            }
            catch (Exception ex)
            {
                MessageBox.ErrorQuery(app, "Configuration Error", ex.Message, "OK");
            }
        }

        private async Task SaveConfigAsync(bool restart)
        {
            if (_configDocument == null) return;
            var previousStatus = _lastStatus;
            try
            {
                var json = CurrentConfigForSave();
                var validation = TransactionalConfigStore.Validate(json);
                if (!validation.Valid)
                {
                    app.Invoke(() => MessageBox.ErrorQuery(app, "Configuration Error", validation.Error ?? "Invalid configuration.", "OK"));
                    return;
                }
                if (MessageBox.Query(app, restart ? "Save and Restart" : "Save Configuration",
                        restart ? "Save hydra.conf and restart Hydra?" : "Save hydra.conf?", "Save", "Cancel") != 0)
                    return;

                SetCommandBusy(true, restart ? "Saving configuration and restarting Hydra…" : "Saving configuration…");
                _configDocument = _connected
                    ? await _client.SaveConfigAsync(new SaveConfigRequest(_configDocument.Revision, json, restart), _cancel.Token)
                    : await _offlineStore.SaveAsync(_configDocument.Revision, json, _cancel.Token);
                _configWithSecrets = _configDocument.Json;
                _secretsRevealed = false;
                _configMaskFailed = false;
                app.Invoke(() =>
                {
                    _config.Text = ConfigSecretMask.Mask(_configDocument.Json);
                    LoadGuidedConfig(_configDocument.Json);
                });
                if (restart && _connected)
                    await WaitForRestartAsync(previousStatus);
                else
                    SetCommandBusy(false, restart ? "Configuration saved. Start Hydra to apply it." : "Configuration saved.", "Accent");
            }
            catch (Exception ex)
            {
                app.Invoke(() =>
                {
                    SetCommandBusy(false, $"Save failed: {ex.Message}", "Error");
                    MessageBox.ErrorQuery(app, "Save Failed", ex.Message, "OK");
                });
            }
        }

        private string CurrentConfigForSave()
        {
            if (_guidedMode)
            {
                CommitGuidedFields();
                return _guidedConfig?.ToJson() ?? throw new InvalidOperationException("Configuration form has not loaded.");
            }
            return TextConfigForSave();
        }

        private string TextConfigForSave() => _secretsRevealed || _configWithSecrets == null
            ? _config.Text
            : ConfigSecretMask.Restore(_config.Text, _configWithSecrets);

        private void SwitchConfigMode(bool guided)
        {
            try
            {
                if (guided == _guidedMode) return;
                if (guided)
                {
                    var json = TextConfigForSave();
                    LoadGuidedConfig(json);
                    _configWithSecrets = json;
                }
                else
                {
                    CommitGuidedFields();
                    var json = _guidedConfig?.ToJson() ?? throw new InvalidOperationException("Configuration form has not loaded.");
                    _configWithSecrets = json;
                    _secretsRevealed = false;
                    _configMaskFailed = false;
                    _config.Text = ConfigSecretMask.Mask(json);
                }
                ShowConfigMode(guided);
            }
            catch (Exception ex)
            {
                MessageBox.ErrorQuery(app, "Configuration Error", ex.Message, "OK");
            }
        }

        private void ShowConfigMode(bool guided)
        {
            _guidedMode = guided;
            _configForm.Visible = guided;
            _configText.Visible = !guided;
            _formModeButton.Text = "_Form";
            _textModeButton.Text = "_Text";
            _formModeButton.SchemeName = guided ? "Accent" : "Base";
            _textModeButton.SchemeName = guided ? "Base" : "Accent";
            _revealSecrets.Visible = !guided;
            _configHelp.Text = guided
                ? "Form mode: Move focus or hover over an option to see what it does."
                : "Text mode: Complete JSON is editable; secrets remain masked by default.";
        }

        private void LoadGuidedConfig(string json)
        {
            _guidedConfig = GuidedConfigDocument.Parse(json);
            _guidedProfileIndex = Math.Clamp(_guidedProfileIndex, 0, Math.Max(0, _guidedConfig.ProfileCount - 1));
            var root = _guidedConfig.ReadRoot();
            _rootName.Text = root.Name ?? "";
            _rootLogLevel.Text = root.LogLevel;
            _rootProfileOverride.Text = root.ProfileOverride ?? "";
            SetChecked(_rootAutoUpdate, root.AutoUpdate);
            SetChecked(_rootDebugShield, root.DebugShield);
            SetChecked(_rootDebugMouse, root.DebugMouse);
            LoadGuidedProfile();
        }

        private void LoadGuidedProfile()
        {
            if (_guidedConfig == null || _guidedConfig.ProfileCount == 0)
            {
                _profilePosition.Text = "No profiles";
                _previousProfile.Enabled = false;
                _nextProfile.Enabled = false;
                return;
            }
            var profile = _guidedConfig.ReadProfile(_guidedProfileIndex);
            _profilePosition.Text = $"{_guidedProfileIndex + 1}/{_guidedConfig.ProfileCount}  {_guidedConfig.ProfileLabel(_guidedProfileIndex)}";
            _previousProfile.Enabled = _guidedProfileIndex > 0;
            _nextProfile.Enabled = _guidedProfileIndex < _guidedConfig.ProfileCount - 1;
            _profileName.Text = profile.ProfileName ?? "";
            _profileMode.Text = profile.Mode;
            _conditionSsid.Text = profile.Ssid ?? "";
            _conditionScreens.Text = profile.ScreenCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            _conditionPower.Text = profile.IsPluggedIn switch { true => "yes", false => "no", null => "any" };
            _networkConfig.Text = profile.NetworkConfig ?? "";
            _embeddedServer.Text = profile.EmbeddedServer ?? "";
            _embeddedPassword.Text = profile.EmbeddedPassword ?? "";
            _embeddedPort.Text = profile.EmbeddedPort?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            _embeddedServerPassword.Text = profile.EmbeddedServerPassword ?? "";
            _mouseScale.Text = profile.MouseScale?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            _relativeMouseScale.Text = profile.RelativeMouseScale?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            _deadCorners.Text = profile.DeadCorners?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            SetChecked(_hideCursor, profile.HideCursor);
            SetChecked(_remoteOnly, profile.RemoteOnly);
            SetChecked(_syncScreensaver, profile.SyncScreensaver);
            SetChecked(_screenLockPropagation, profile.ScreenLockPropagation);
            SetChecked(_accelerateMouseWheel, profile.AccelerateMouseWheel);
            SetChecked(_unicodeKeyRepeat, profile.UnicodeKeyRepeat);
            _advancedSummary.Text = $"Advanced\nHosts: {profile.HostCount}   Screen Definitions: {profile.ScreenDefinitionCount}\nUse Text mode to edit hosts, neighbours and per-screen matching.";
        }

        private void CommitGuidedFields()
        {
            if (_guidedConfig == null) throw new InvalidOperationException("Configuration form has not loaded.");
            _guidedConfig.WriteRoot(new GuidedRootFields(
                _rootName.Text, _rootProfileOverride.Text, _rootLogLevel.Text,
                IsChecked(_rootAutoUpdate), IsChecked(_rootDebugShield), IsChecked(_rootDebugMouse)));
            if (_guidedConfig.ProfileCount == 0) return;
            _guidedConfig.WriteProfile(_guidedProfileIndex, new GuidedProfileFields(
                _profileName.Text,
                _profileMode.Text.Trim(),
                _conditionSsid.Text,
                GuidedConfigDocument.ParseInt(_conditionScreens.Text, "Screen count"),
                ParsePower(_conditionPower.Text),
                _networkConfig.Text,
                _embeddedServer.Text,
                _embeddedPassword.Text,
                GuidedConfigDocument.ParseInt(_embeddedPort.Text, "Local relay port"),
                _embeddedServerPassword.Text,
                IsChecked(_hideCursor),
                IsChecked(_remoteOnly),
                IsChecked(_syncScreensaver),
                IsChecked(_screenLockPropagation),
                IsChecked(_accelerateMouseWheel),
                IsChecked(_unicodeKeyRepeat),
                GuidedConfigDocument.ParseDecimal(_mouseScale.Text, "Mouse scale"),
                GuidedConfigDocument.ParseDecimal(_relativeMouseScale.Text, "Relative mouse scale"),
                GuidedConfigDocument.ParseInt(_deadCorners.Text, "Dead corners"),
                0,
                0));
        }

        private void ChangeGuidedProfile(int delta)
        {
            try
            {
                if (_guidedConfig == null || _guidedConfig.ProfileCount == 0) return;
                CommitGuidedFields();
                _guidedProfileIndex = Math.Clamp(_guidedProfileIndex + delta, 0, _guidedConfig.ProfileCount - 1);
                LoadGuidedProfile();
            }
            catch (Exception ex)
            {
                MessageBox.ErrorQuery(app, "Configuration Error", ex.Message, "OK");
            }
        }

        private static bool? ParsePower(string value) => value.Trim().ToLowerInvariant() switch
        {
            "" or "any" => null,
            "yes" or "true" or "on" => true,
            "no" or "false" or "off" => false,
            _ => throw new InvalidOperationException("Power condition must be any, yes, or no.")
        };

        private static bool IsChecked(CheckBox box) => box.Value == CheckState.Checked;
        private static void SetChecked(CheckBox box, bool value) => box.Value = value ? CheckState.Checked : CheckState.UnChecked;

        private void ToggleSecrets(Button button)
        {
            if (_configWithSecrets == null) return;
            try
            {
                if (_secretsRevealed)
                {
                    _configWithSecrets = _config.Text;
                    _config.Text = ConfigSecretMask.Mask(_config.Text);
                    _configMaskFailed = false;
                    _secretsRevealed = false;
                    button.Text = "_Reveal Secrets";
                }
                else
                {
                    if (MessageBox.Query(app, "Reveal Secrets", "Show relay credentials in the terminal?", "Reveal", "Cancel") != 0)
                        return;
                    _config.Text = _configMaskFailed
                        ? _configWithSecrets
                        : ConfigSecretMask.Restore(_config.Text, _configWithSecrets);
                    _secretsRevealed = true;
                    button.Text = "_Hide Secrets";
                }
            }
            catch (Exception ex)
            {
                MessageBox.ErrorQuery(app, "Configuration Error", ex.Message, "OK");
            }
        }

        private async Task RunCommandAsync(Func<Task<CommandResult>> command, CommandKind kind)
        {
            if (!_liveControlsReady)
            {
                app.Invoke(() => MessageBox.Query(app, "Management Unavailable",
                    "This TUI is not connected to Hydra. The running Hydra process has not been interrupted.", "OK"));
                return;
            }
            if (_commandBusy) return;
            var previousStatus = _lastStatus;
            SetCommandBusy(true, kind == CommandKind.RestartHydra ? "Restarting Hydra…" : "Reconnecting relay…");
            try
            {
                var result = await command();
                if (!result.Accepted)
                {
                    app.Invoke(() =>
                    {
                        SetCommandBusy(false, result.Message, "Error");
                        MessageBox.ErrorQuery(app, "Command Unavailable", result.Message, "OK");
                    });
                    return;
                }
                if (kind == CommandKind.RestartHydra)
                    await WaitForRestartAsync(previousStatus);
                else
                    await WaitForRelayAsync();
            }
            catch (Exception ex)
            {
                app.Invoke(() =>
                {
                    SetCommandBusy(false, $"Command failed: {ex.Message}", "Error");
                    MessageBox.ErrorQuery(app, "Command Failed", ex.Message, "OK");
                });
            }
        }

        private async Task WaitForRestartAsync(HydraStatusSnapshot? previousStatus)
        {
            SetActivity("Restart requested — waiting for Hydra to come back…", "Accent");
            await WaitForStatusAsync(status => previousStatus == null || HasRestarted(previousStatus, status),
                status => $"Hydra restarted and reconnected (PID {status.ProcessId}).");
        }

        private async Task WaitForRelayAsync()
        {
            SetActivity("Relay reconnect requested — waiting for the connection…", "Accent");
            await WaitForStatusAsync(status => status.RelayConnected && status.RelayConnection != null,
                _ => "Relay reconnected.");
        }

        private async Task WaitForStatusAsync(Func<HydraStatusSnapshot, bool> complete, Func<HydraStatusSnapshot, string> message)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTimeOffset.UtcNow < deadline && !_cancel.IsCancellationRequested)
            {
                await Task.Delay(300, _cancel.Token);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
                    timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
                    var status = await _client.GetStatusAsync(timeout.Token);
                    if (!complete(status)) continue;
                    _connected = true;
                    _helloComplete = false;
                    _lastStatus = status;
                    app.Invoke(() =>
                    {
                        Render(status, new ManagementLogPage(_logCursor, _logCursor, []));
                        SetCommandBusy(false, message(status), "Accent");
                    });
                    return;
                }
                catch (Exception) when (DateTimeOffset.UtcNow < deadline)
                {
                    // A restart deliberately removes the management endpoint for a moment.
                }
            }
            app.Invoke(() => SetCommandBusy(false,
                "The command was accepted, but Hydra did not report completion within 15 seconds.", "Error"));
        }

        private void SetCommandBusy(bool busy, string message, string scheme = "Accent")
        {
            void Apply()
            {
                _commandBusy = busy;
                SetLiveControls(_connected && !busy);
                SetActivity(message, scheme);
            }
            app.Invoke(Apply);
        }

        private void SetActivity(string message, string scheme)
        {
            _activity.SchemeName = scheme;
            SetText(_activity, message);
        }

        private void SetLiveControls(bool enabled)
        {
            _liveControlsReady = enabled;
            _reconnect.Enabled = enabled && !_commandBusy;
            _restart.Enabled = enabled && !_commandBusy;
        }

        private enum CommandKind { ReconnectRelay, RestartHydra }

        private static string FormatOverview(HydraStatusSnapshot s)
        {
            var route = s.Router == null ? "n/a" : s.Router.IsRemote ? $"{s.Router.ActiveHost}/{s.Router.ActiveScreen}" : "local";
            var relay = s.RelayConnection;
            var connection = relay == null
                ? "  Network       unavailable"
                : $"""
                  Network       {relay.InterfaceType} ({relay.InterfaceName})
                  Local socket  {relay.LocalAddress}:{relay.LocalPort}
                  Relay         {relay.RelayHost} → {relay.RemoteAddress}:{relay.RemotePort}
                  Connected for {DateTimeOffset.UtcNow - relay.ConnectedAt:g}
                  Relay traffic ↑ {FormatBytes(relay.BytesSent)} / {relay.MessagesSent} msg   ↓ {FormatBytes(relay.BytesReceived)} / {relay.MessagesReceived} msg
                  Attempts      {relay.ConnectionAttempts}
                """;
            var activeAdapters = s.ActiveNetworkAdapters ?? [];
            var adapters = activeAdapters.Count == 0
                ? "  (none detected)"
                : string.Join('\n', activeAdapters.Select(adapter =>
                    $"  {(adapter.HasGateway ? "◆" : "◇")} {adapter.Type,-12} {adapter.Name,-10} {string.Join(", ", adapter.Addresses)}"));
            var relayPeers = s.EmbeddedRelayPeers ?? [];
            var embeddedPeers = relayPeers.Count == 0
                ? "  (not hosting an embedded relay)"
                : string.Join('\n', relayPeers.Select(peer =>
                    $"  ● {peer.HostName,-16} {peer.InterfaceType} ({peer.InterfaceName})  {peer.RemoteAddress} → {peer.LocalAddress}{(peer.HostName.Equals(s.HostName, StringComparison.OrdinalIgnoreCase) ? "  [this Hydra]" : "")}"));
            return $"""
                Runtime
                  Process       {s.ProcessId}
                  Uptime        {TimeSpan.FromSeconds(s.UptimeSeconds):g}
                  Profile       {s.ProfileName ?? "<no matching profile>"}
                  Mode          {s.Mode}
                  Config        {s.ConfigPath}

                Health
                  Relay state   {(s.RelayConnected ? "connected" : "disconnected")}
                  Dormant       {(s.Dormant ? "yes" : "no")}
                  Local screens {s.LocalScreens.Count}
                  Peers         {s.Peers.Count}

                Connection
                {connection}

                Active Network Links  (◆ has a gateway)
                {adapters}

                Embedded Relay Clients  (actual inbound interface)
                {embeddedPeers}

                Routing
                  Active route  {route}
                  Screen lock   {(s.Router?.LockedToScreen == true ? "locked" : "unlocked")}
                  Mouse mode    {(s.Router?.RelativeMouse == true ? "relative" : "absolute")}
                """;
        }

        private static string FormatBytes(long bytes) => bytes switch
        {
            >= 1024 * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.0} GiB",
            >= 1024 * 1024 => $"{bytes / (1024d * 1024):0.0} MiB",
            >= 1024 => $"{bytes / 1024d:0.0} KiB",
            _ => $"{bytes} B"
        };

        private static string FormatPeers(HydraStatusSnapshot s)
        {
            var output = new StringBuilder();
            output.AppendLine("LOCAL SCREENS");
            foreach (var screen in s.LocalScreens)
                output.AppendLine($"  {screen.Name,-28} {screen.Width,5}×{screen.Height,-5} scale {screen.MouseScale}");
            if (s.LocalScreens.Count == 0) output.AppendLine("  (none detected)");
            output.AppendLine().AppendLine("PEERS");
            foreach (var peer in s.Peers)
            {
                output.AppendLine($"  {(peer.Connected ? "●" : "○")} {peer.Name}  [{peer.Platform}]  {peer.Screens.Count} screen(s)");
                foreach (var screen in peer.Screens)
                    output.AppendLine($"      {screen.Name,-24} {screen.Width,5}×{screen.Height,-5} scale {screen.MouseScale}");
            }
            if (s.Peers.Count == 0) output.AppendLine("  (no peers online)");
            return output.ToString();
        }

        private string FormatDiagnostics(Exception? error = null) => $"""
            Management     {(_connected ? "connected" : "unavailable")}
            Protocol       {ManagementProtocol.Version}
            Config path    {configPath}
            Config rev     {_configDocument?.Revision ?? _lastStatus?.ConfigRevision ?? "unknown"}
            Last snapshot  {_lastStatus?.CapturedAt.ToLocalTime().ToString("O") ?? "none"}
            Log cursor     {_logCursor}
            Last error     {error?.Message ?? "none"}

            The management endpoint is local-only. Secrets, clipboard data, typed characters and
            file-transfer content are not included in status or the TUI log buffer.
            """;

        private static string ShortCategory(string category) => category.Length <= 24 ? category : category[^24..];

        private static Editor ReadOnlyEditor() => new()
        {
            ReadOnly = true,
            WordWrap = false,
            ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar | ViewportSettingsFlags.HasHorizontalScrollBar
        };

        public void Dispose()
        {
            app.Mouse.MouseEvent -= HandleMainTabMouse;
            _cancel.Cancel();
            _cancel.Dispose();
        }
    }
}
