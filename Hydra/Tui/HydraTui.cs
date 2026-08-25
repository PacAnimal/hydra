using System.Text;
using System.Net.Sockets;
using Hydra.Config;
using Hydra.Management;
using Terminal.Gui.App;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Hydra;

internal static class HydraTui
{
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
        private readonly Editor _diagnostics = ReadOnlyEditor();
        private readonly Label _connection = new() { Text = "Connecting…", X = 1, Y = 0, Width = Dim.Fill() };
        private readonly Button _reconnect = new() { Text = "_Reconnect relay", Enabled = false };
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
        private GuidedConfigDocument? _guidedConfig;
        private int _guidedProfileIndex;

        private readonly TextField _rootName = new();
        private readonly TextField _rootLogLevel = new();
        private readonly TextField _rootProfileOverride = new();
        private readonly CheckBox _rootAutoUpdate = new() { Text = "Auto update" };
        private readonly CheckBox _rootDebugShield = new() { Text = "Debug shield" };
        private readonly CheckBox _rootDebugMouse = new() { Text = "Debug mouse" };
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
        private readonly CheckBox _hideCursor = new() { Text = "Hide cursor" };
        private readonly CheckBox _remoteOnly = new() { Text = "Remote only" };
        private readonly CheckBox _syncScreensaver = new() { Text = "Sync screensaver" };
        private readonly CheckBox _screenLockPropagation = new() { Text = "Propagate screen lock" };
        private readonly CheckBox _accelerateMouseWheel = new() { Text = "Accelerate wheel" };
        private readonly CheckBox _unicodeKeyRepeat = new() { Text = "Unicode key repeat" };
        private readonly Label _advancedSummary = new();

        internal void Build()
        {
            window.Add(_connection);
            var tabs = new Tabs { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(2) };
            tabs.Add(
                BuildOverviewTab(),
                BuildTextTab("_Peers & Screens", _peers),
                BuildTextTab("_Logs", _logs),
                BuildConfigTab(),
                BuildTextTab("_Diagnostics", _diagnostics),
                BuildHelpTab());
            window.Add(tabs);

            var status = new StatusBar([
                new Shortcut(Application.GetDefaultKey(Command.Quit), "Quit", () => window.RequestStop()),
                new Shortcut(Key.F5, "Refresh", () => _ = RefreshAsync()),
                new Shortcut(Key.F1, "Help", () => tabs.Value = tabs.TabCollection.Last())
            ]);
            window.Add(status);

            app.AddTimeout(TimeSpan.FromSeconds(1), () =>
            {
                _ = RefreshAsync();
                return true;
            });
            _ = LoadConfigAsync();
            _ = RefreshAsync();
        }

        private FrameView BuildOverviewTab()
        {
            var tab = BuildTextTab("_Overview", _overview);
            _overview.Height = Dim.Fill(3);
            _reconnect.X = 1;
            _reconnect.Y = Pos.AnchorEnd(2);
            _reconnect.Accepting += (_, e) => { e.Handled = true; _ = RunCommandAsync(() => _client.ReconnectRelayAsync(_cancel.Token)); };
            _restart.X = Pos.Right(_reconnect) + 2;
            _restart.Y = Pos.Top(_reconnect);
            _restart.Accepting += (_, e) =>
            {
                e.Handled = true;
                if (MessageBox.Query(app, "Restart Hydra", "Restart the running Hydra process?", "Restart", "Cancel") == 0)
                    _ = RunCommandAsync(() => _client.RestartHydraAsync(_cancel.Token));
            };
            tab.Add(_reconnect, _restart);
            return tab;
        }

        private FrameView BuildConfigTab()
        {
            var tab = new FrameView { Title = "_Configuration", Width = Dim.Fill(), Height = Dim.Fill() };
            var formMode = new Button { Text = "_Form", X = 1, Y = 0 };
            var textMode = new Button { Text = "_Text", X = Pos.Right(formMode) + 2, Y = 0 };
            var hint = new Label { Text = "Form preserves advanced fields; Text exposes the complete JSON", X = Pos.Right(textMode) + 3, Y = 0, Width = Dim.Fill() };
            formMode.Accepting += (_, e) => { e.Handled = true; SwitchConfigMode(guided: true); };
            textMode.Accepting += (_, e) => { e.Handled = true; SwitchConfigMode(guided: false); };

            _configForm.X = _configText.X = 0;
            _configForm.Y = _configText.Y = 2;
            _configForm.Width = _configText.Width = Dim.Fill();
            _configForm.Height = _configText.Height = Dim.Fill();
            BuildGuidedConfigForm();

            _config.X = 0;
            _config.Y = 0;
            _config.Width = Dim.Fill();
            _config.Height = Dim.Fill(3);
            var validate = new Button { Text = "_Validate", X = 1, Y = Pos.AnchorEnd(2) };
            validate.Accepting += (_, e) => { e.Handled = true; ValidateConfig(); };
            var reload = new Button { Text = "Re_load", X = Pos.Right(validate) + 2, Y = Pos.Top(validate) };
            reload.Accepting += (_, e) => { e.Handled = true; _ = LoadConfigAsync(); };
            var reveal = new Button { Text = "_Reveal secrets", X = Pos.Right(reload) + 2, Y = Pos.Top(validate) };
            reveal.Accepting += (_, e) =>
            {
                e.Handled = true;
                ToggleSecrets(reveal);
            };
            var save = new Button { Text = "_Save", X = Pos.Right(reveal) + 2, Y = Pos.Top(validate) };
            save.Accepting += (_, e) => { e.Handled = true; _ = SaveConfigAsync(restart: false); };
            var apply = new Button { Text = "Save && _restart", X = Pos.Right(save) + 2, Y = Pos.Top(validate) };
            apply.Accepting += (_, e) => { e.Handled = true; _ = SaveConfigAsync(restart: true); };
            _configText.Add(_config, validate, reload, reveal, save, apply);
            tab.Add(formMode, textMode, hint, _configForm, _configText);
            return tab;
        }

        private void BuildGuidedConfigForm()
        {
            _configForm.Add(new Label { Text = "GLOBAL", X = 1, Y = 0 });
            AddField(_configForm, "Machine name", _rootName, 1, 1, 26);
            AddField(_configForm, "Log level", _rootLogLevel, 1, 2, 26);
            AddField(_configForm, "Force profile", _rootProfileOverride, 1, 3, 26);
            _rootAutoUpdate.X = 17; _rootAutoUpdate.Y = 4;
            _rootDebugShield.X = 17; _rootDebugShield.Y = 5;
            _rootDebugMouse.X = 17; _rootDebugMouse.Y = 6;

            var previous = new Button { Text = "_Previous", X = 1, Y = 8 };
            var next = new Button { Text = "_Next", X = Pos.Right(previous) + 2, Y = 8 };
            _profilePosition.X = Pos.Right(next) + 2; _profilePosition.Y = 8; _profilePosition.Width = 28;
            previous.Accepting += (_, e) => { e.Handled = true; ChangeGuidedProfile(-1); };
            next.Accepting += (_, e) => { e.Handled = true; ChangeGuidedProfile(1); };
            AddField(_configForm, "Profile name", _profileName, 1, 9, 26);
            AddField(_configForm, "Mode", _profileMode, 1, 10, 26);
            AddField(_configForm, "SSID condition", _conditionSsid, 1, 11, 26);
            AddField(_configForm, "Screen count", _conditionScreens, 1, 12, 26);
            AddField(_configForm, "Power (any/yes/no)", _conditionPower, 1, 13, 26);
            AddField(_configForm, "Mouse scale", _mouseScale, 1, 14, 26);
            AddField(_configForm, "Relative scale", _relativeMouseScale, 1, 15, 26);
            AddField(_configForm, "Dead corners px", _deadCorners, 1, 16, 26);

            _hideCursor.X = 17; _hideCursor.Y = 18;
            _remoteOnly.X = 17; _remoteOnly.Y = 19;
            _syncScreensaver.X = 17; _syncScreensaver.Y = 20;
            _screenLockPropagation.X = 17; _screenLockPropagation.Y = 21;
            _accelerateMouseWheel.X = 17; _accelerateMouseWheel.Y = 22;
            _unicodeKeyRepeat.X = 17; _unicodeKeyRepeat.Y = 23;

            const int right = 51;
            _configForm.Add(new Label { Text = "RELAY", X = right, Y = 0 });
            AddField(_configForm, "Network config", _networkConfig, right, 1, 34);
            AddField(_configForm, "Embedded URL", _embeddedServer, right, 3, 34);
            AddField(_configForm, "Password", _embeddedPassword, right, 4, 34);
            AddField(_configForm, "Local relay port", _embeddedPort, right, 6, 34);
            AddField(_configForm, "Password", _embeddedServerPassword, right, 7, 34);
            _configForm.Add(new Label { Text = "Secrets are masked while typing.", X = right, Y = 9 });
            _advancedSummary.X = right; _advancedSummary.Y = 11; _advancedSummary.Width = Dim.Fill(1); _advancedSummary.Height = 4;
            _configForm.Add(_advancedSummary);

            var validate = new Button { Text = "_Validate", X = 1, Y = Pos.AnchorEnd(2) };
            var reload = new Button { Text = "Re_load", X = Pos.Right(validate) + 2, Y = Pos.Top(validate) };
            var save = new Button { Text = "_Save", X = Pos.Right(reload) + 2, Y = Pos.Top(validate) };
            var apply = new Button { Text = "Save && _restart", X = Pos.Right(save) + 2, Y = Pos.Top(validate) };
            validate.Accepting += (_, e) => { e.Handled = true; ValidateConfig(); };
            reload.Accepting += (_, e) => { e.Handled = true; _ = LoadConfigAsync(); };
            save.Accepting += (_, e) => { e.Handled = true; _ = SaveConfigAsync(restart: false); };
            apply.Accepting += (_, e) => { e.Handled = true; _ = SaveConfigAsync(restart: true); };
            _configForm.Add(_rootAutoUpdate, _rootDebugShield, _rootDebugMouse, previous, next,
                _hideCursor, _remoteOnly, _syncScreensaver, _screenLockPropagation, _accelerateMouseWheel,
                _unicodeKeyRepeat, validate, reload, save, apply);
        }

        private static void AddField(View parent, string label, TextField field, int x, int y, int width)
        {
            parent.Add(new Label { Text = label, X = x, Y = y });
            field.X = x + 17;
            field.Y = y;
            field.Width = width;
            parent.Add(field);
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
            _connection.Text = $"● Connected  │  Hydra {status.Version}  │  {status.HostName}  │  {status.ProfileName ?? "idle"} / {status.Mode}";
            _overview.Text = FormatOverview(status);
            _peers.Text = FormatPeers(status);
            if (page.Entries.Count > 0)
            {
                foreach (var entry in page.Entries)
                {
                    _visibleLogs.Enqueue($"{entry.Timestamp.ToLocalTime():HH:mm:ss} {entry.Level,-11} {ShortCategory(entry.Category),-24} {entry.Message}");
                    while (_visibleLogs.Count > ManagementLogBuffer.Capacity)
                        _visibleLogs.Dequeue();
                }
                _logs.Text = string.Join('\n', _visibleLogs);
            }
            _diagnostics.Text = FormatDiagnostics();
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
                    MessageBox.ErrorQuery(app, "Configuration error", result.Error ?? "Invalid configuration.", "OK");
            }
            catch (Exception ex)
            {
                MessageBox.ErrorQuery(app, "Configuration error", ex.Message, "OK");
            }
        }

        private async Task SaveConfigAsync(bool restart)
        {
            if (_configDocument == null) return;
            try
            {
                var json = CurrentConfigForSave();
                var validation = TransactionalConfigStore.Validate(json);
                if (!validation.Valid)
                {
                    app.Invoke(() => MessageBox.ErrorQuery(app, "Configuration error", validation.Error ?? "Invalid configuration.", "OK"));
                    return;
                }
                if (MessageBox.Query(app, restart ? "Save and restart" : "Save configuration",
                        restart ? "Save hydra.conf and restart Hydra?" : "Save hydra.conf?", "Save", "Cancel") != 0)
                    return;

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
                app.Invoke(() => MessageBox.Query(app, "Configuration", restart ? "Saved. Hydra is restarting." : "Saved.", "OK"));
            }
            catch (Exception ex)
            {
                app.Invoke(() => MessageBox.ErrorQuery(app, "Save failed", ex.Message, "OK"));
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
                MessageBox.ErrorQuery(app, "Configuration error", ex.Message, "OK");
            }
        }

        private void ShowConfigMode(bool guided)
        {
            _guidedMode = guided;
            _configForm.Visible = guided;
            _configText.Visible = !guided;
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
                return;
            }
            var profile = _guidedConfig.ReadProfile(_guidedProfileIndex);
            _profilePosition.Text = $"{_guidedProfileIndex + 1}/{_guidedConfig.ProfileCount}  {_guidedConfig.ProfileLabel(_guidedProfileIndex)}";
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
            _advancedSummary.Text = $"ADVANCED\nHosts: {profile.HostCount}   Screen definitions: {profile.ScreenDefinitionCount}\nUse Text mode to edit hosts, neighbours and per-screen matching.";
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
                _guidedProfileIndex = (_guidedProfileIndex + delta + _guidedConfig.ProfileCount) % _guidedConfig.ProfileCount;
                LoadGuidedProfile();
            }
            catch (Exception ex)
            {
                MessageBox.ErrorQuery(app, "Configuration error", ex.Message, "OK");
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
                    button.Text = "_Reveal secrets";
                }
                else
                {
                    if (MessageBox.Query(app, "Reveal secrets", "Show relay credentials in the terminal?", "Reveal", "Cancel") != 0)
                        return;
                    _config.Text = _configMaskFailed
                        ? _configWithSecrets
                        : ConfigSecretMask.Restore(_config.Text, _configWithSecrets);
                    _secretsRevealed = true;
                    button.Text = "_Hide secrets";
                }
            }
            catch (Exception ex)
            {
                MessageBox.ErrorQuery(app, "Configuration error", ex.Message, "OK");
            }
        }

        private async Task RunCommandAsync(Func<Task<CommandResult>> command)
        {
            if (!_liveControlsReady)
            {
                app.Invoke(() => MessageBox.Query(app, "Management unavailable",
                    "This TUI is not connected to Hydra. The running Hydra process has not been interrupted.", "OK"));
                return;
            }
            try
            {
                var result = await command();
                app.Invoke(() => MessageBox.Query(app, result.Accepted ? "Hydra" : "Command unavailable", result.Message, "OK"));
            }
            catch (Exception ex)
            {
                app.Invoke(() => MessageBox.ErrorQuery(app, "Command failed", ex.Message, "OK"));
            }
        }

        private void SetLiveControls(bool enabled)
        {
            _liveControlsReady = enabled;
            _reconnect.Enabled = enabled;
            _restart.Enabled = enabled;
        }

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
            var adapters = s.ActiveNetworkAdapters.Count == 0
                ? "  (none detected)"
                : string.Join('\n', s.ActiveNetworkAdapters.Select(adapter =>
                    $"  {(adapter.HasGateway ? "◆" : "◇")} {adapter.Type,-12} {adapter.Name,-10} {string.Join(", ", adapter.Addresses)}"));
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

                Active network links  (◆ has a gateway)
                {adapters}

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
            _cancel.Cancel();
            _cancel.Dispose();
        }
    }
}
