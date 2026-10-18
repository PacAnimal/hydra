using ByteSizeLib;
using Cathedral.Config;
using Microsoft.AspNetCore.SignalR;
using System.Text.Json.Serialization.Metadata;

namespace Styx;

public static class StyxSignalRExtensions
{
    // hub and protocol wiring, shared by the standalone server and any in-process host, so the two can't
    // drift apart on limits or on which serializers a client may speak
    public static IServiceCollection AddStyxSignalR(this IServiceCollection services, int parallelInvocations)
    {
        services.AddSingleton<HubLifetimeManager<ParallelStyxHub>, SharedHubLifetimeManager<ParallelStyxHub>>();

        services.AddSignalR(options =>
        {
            options.KeepAliveInterval = TimeSpan.FromSeconds(Constants.KeepAliveSeconds);
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(Constants.ClientTimeoutSeconds);
            options.EnableDetailedErrors = true;
            options.MaximumReceiveMessageSize = (long)ByteSize.FromMebiBytes(Constants.MaxMessageMebiBytes).Bytes;
            options.MaximumParallelInvocationsPerClient = Constants.MaxParallelInvocations;
        }).AddJsonProtocol(options =>
        {
            // "required" is a C# construction guarantee, not a wire contract: a client that omits a member
            // should get a refusal it can read from the hub, not an argument-binding error from the parser
            options.PayloadSerializerOptions.TypeInfoResolver =
                (options.PayloadSerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver())
                .WithAddedModifier(static typeInfo =>
                {
                    foreach (var property in typeInfo.Properties)
                        property.IsRequired = false;
                });
        }).AddMessagePackProtocol(options =>
        {
            // members match in any casing; StyxWireFormatTests pins the encoding
            options.SerializerOptions = SaneMessagePack.InteropOptions;
        }).AddHubOptions<ParallelStyxHub>(hub => hub.MaximumParallelInvocationsPerClient = parallelInvocations);

        return services;
    }
}
