using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cathedral.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Hydra.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed partial class SasService(ILogger<SasService> log) : SimpleHostedService(log, TimeSpan.Zero)
{
    private SafeFileHandle? _sasEvent;

    protected override Task Execute(CancellationToken cancel)
    {
        _sasEvent ??= Win32Session.CreateGlobalEvent("HydraSendSAS", manualReset: false);
        if (Win32Session.WaitForEvent(_sasEvent, 1000))
            SendSAS(asUser: false);
        return Task.CompletedTask;
    }

    protected override Task OnShutdown(CancellationToken cancel)
    {
        _sasEvent?.Dispose();
        return Task.CompletedTask;
    }

    // asUser=false means the call comes from a service (SYSTEM) — required for it to work
    // ReSharper disable once InconsistentNaming
    [LibraryImport("sas.dll")]
    private static partial void SendSAS([MarshalAs(UnmanagedType.Bool)] bool asUser);
}
