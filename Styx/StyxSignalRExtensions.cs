using ByteSizeLib;
using MessagePack;
using MessagePack.Resolvers;
using Cathedral.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Styx;

public static class StyxSignalRExtensions
{
    // hub and protocol wiring, shared by the standalone server and any in-process host, so the two can't
    // drift apart on limits or on which serializers a client may speak
    public static IServiceCollection AddStyxSignalR(this IServiceCollection services)
    {
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
            // a client picks its own member-name casing, and MessagePack would otherwise match keys by exact
            // bytes and hand the hub an argument with unset members. Only the resolver is borrowed, not
            // SaneMessagePack's options: those also write enums as strings and Guids as raw bytes, which would
            // change this hub's wire format the day a method carries either.
            options.SerializerOptions = MessagePackSerializerOptions.Standard
                .WithResolver(CompositeResolver.Create(
                    [],
                    [new CaseInsensitiveObjectResolver(ContractlessStandardResolver.Instance),
                     ContractlessStandardResolver.Instance]))
                .WithSecurity(MessagePackSecurity.UntrustedData);
        });

        return services;
    }
}
