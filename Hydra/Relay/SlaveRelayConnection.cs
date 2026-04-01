using Cathedral.Extensions;
using Hydra.Config;
using Hydra.Platform;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

public sealed class SlaveRelayConnection : RelayConnection
{
    private readonly IPlatformOutput _output;
    private readonly SlaveLogForwarder _logForwarder;
    private readonly ILogger<RelayConnection> _log;
    private readonly ScreenRect _localScreen;

    // ReSharper disable once ConvertToPrimaryConstructor
#pragma warning disable IDE0290
    public SlaveRelayConnection(HydraConfig config, ILogger<RelayConnection> log, IPlatformOutput output, SlaveLogForwarder logForwarder)
        : base(config, log)
    {
        _output = output;
        _logForwarder = logForwarder;
        _log = log;
        _localScreen = output.GetPrimaryScreenBounds();
    }
#pragma warning restore IDE0290

    protected override void OnAuthenticated()
    {
        _log.LogInformation("Local screen: {W}x{H}", _localScreen.Width, _localScreen.Height);
    }

    protected override Task OnReceive(string sourceHost, MessageKind kind, string json)
    {
        switch (kind)
        {
            case MessageKind.MasterConfig:
                HandleMasterConfig(sourceHost);
                break;
            case MessageKind.MouseMove:
                var move = json.FromSaneJson<MouseMoveMessage>();
                if (move != null) _output.MoveMouse(move.X, move.Y);
                break;
            case MessageKind.KeyEvent:
                var key = json.FromSaneJson<KeyEventMessage>();
                if (key != null) _output.InjectKey(key);
                break;
            case MessageKind.MouseButton:
                var btn = json.FromSaneJson<MouseButtonMessage>();
                if (btn != null) _output.InjectMouseButton(btn);
                break;
            case MessageKind.MouseScroll:
                var scroll = json.FromSaneJson<MouseScrollMessage>();
                if (scroll != null) _output.InjectMouseScroll(scroll);
                break;
            case MessageKind.EnterScreen:
                var enter = json.FromSaneJson<EnterScreenMessage>();
                if (enter != null) _output.MoveMouse(enter.X, enter.Y);
                break;
            case MessageKind.LeaveScreen:
                break;
        }
        return Task.CompletedTask;
    }

    private void HandleMasterConfig(string masterHost)
    {
        _logForwarder.AddMaster(masterHost);
        _log.LogInformation("Sending screen info to {Master}: {W}x{H}", masterHost, _localScreen.Width, _localScreen.Height);
        var payload = MessageSerializer.Encode(MessageKind.ScreenInfo, new ScreenInfoMessage(_localScreen.Width, _localScreen.Height));
        _ = Send([masterHost], payload).AsTask();
    }
}
