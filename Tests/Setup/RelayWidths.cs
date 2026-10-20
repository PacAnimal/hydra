using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Styx;

namespace Tests.Setup;

/// <summary>
/// How many of a client's invocations each relay path runs at once, read off the options a host composed — never off the
/// constants, since anything configuring <see cref="HubOptions"/> after the relay's own wiring wins.
/// </summary>
public static class RelayWidths
{
    // SignalR reads a hub type's own options once they are set and the global ones otherwise, so /relay is one
    // invocation at a time only while both say so
    public static int[] OrderedPath(IServiceProvider services) =>
    [
        services.GetRequiredService<IOptions<HubOptions>>().Value.MaximumParallelInvocationsPerClient,
        services.GetRequiredService<IOptions<HubOptions<StyxHub>>>().Value.MaximumParallelInvocationsPerClient,
    ];

    public static int ParallelPath(IServiceProvider services) =>
        services.GetRequiredService<IOptions<HubOptions<ParallelStyxHub>>>().Value.MaximumParallelInvocationsPerClient;
}
