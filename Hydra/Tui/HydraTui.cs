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
            var tab = BuildTextTab("_Configuration", _config);
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
            tab.Add(validate, reload, save, apply);
            tab.Add(reveal);
            return tab;
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

                Overview shows the live local Hydra process, active profile, relay and routing state.
                Peers & Screens lists state already known by this Hydra instance.
                Logs are a bounded local monitoring stream and never include clipboard/file-transfer content.
                Configuration edits the exact hydra.conf source. Validate before saving. Save & restart
                atomically writes the file and asks the daemon to restart; external edits are detected.

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
                    app.Invoke(() => _config.Text = masked);
                }
                catch (System.Text.Json.JsonException)
                {
                    _configMaskFailed = true;
                    app.Invoke(() => _config.Text = "Configuration JSON is invalid and cannot be safely masked.\nUse Reveal secrets to inspect and repair the raw document.");
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
                var result = TransactionalConfigStore.Validate(ConfigForSave());
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
                var json = ConfigForSave();
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
                app.Invoke(() => _config.Text = ConfigSecretMask.Mask(_configDocument.Json));
                app.Invoke(() => MessageBox.Query(app, "Configuration", restart ? "Saved. Hydra is restarting." : "Saved.", "OK"));
            }
            catch (Exception ex)
            {
                app.Invoke(() => MessageBox.ErrorQuery(app, "Save failed", ex.Message, "OK"));
            }
        }

        private string ConfigForSave() => _secretsRevealed || _configWithSecrets == null
            ? _config.Text
            : ConfigSecretMask.Restore(_config.Text, _configWithSecrets);

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
            return $"""
                Runtime
                  Process       {s.ProcessId}
                  Uptime        {TimeSpan.FromSeconds(s.UptimeSeconds):g}
                  Profile       {s.ProfileName ?? "<no matching profile>"}
                  Mode          {s.Mode}
                  Config        {s.ConfigPath}

                Health
                  Relay         {(s.RelayConnected ? "connected" : "disconnected")}
                  Dormant       {(s.Dormant ? "yes" : "no")}
                  Local screens {s.LocalScreens.Count}
                  Peers         {s.Peers.Count}

                Routing
                  Active route  {route}
                  Screen lock   {(s.Router?.LockedToScreen == true ? "locked" : "unlocked")}
                  Mouse mode    {(s.Router?.RelativeMouse == true ? "relative" : "absolute")}
                """;
        }

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
