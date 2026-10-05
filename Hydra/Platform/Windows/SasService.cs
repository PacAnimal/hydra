using System.Runtime.Versioning;
using Cathedral.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Hydra.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class SasService(ILogger<SasService> log) : SimpleHostedService(log, TimeSpan.Zero, TimeSpan.FromSeconds(5))
{
    private SafeFileHandle? _sasEvent;

    protected override Task Execute(CancellationToken cancel)
    {
        _sasEvent ??= Win32Session.CreateGlobalEvent("HydraSendSAS", manualReset: false);
        if (Win32Session.WaitForEvent(_sasEvent, 1000))
            NativeMethods.SendSAS(asUser: false);
        return Task.CompletedTask;
    }

    protected override Task OnShutdown(CancellationToken cancel)
    {
        _sasEvent?.Dispose();
        return Task.CompletedTask;
    }
}
