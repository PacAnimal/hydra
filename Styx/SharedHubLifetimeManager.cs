using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using System.Diagnostics.CodeAnalysis;

namespace Styx;

/// <summary>
/// Keeps a hub type's connections in <see cref="StyxHub"/>'s lifetime manager. SignalR gives every hub type a manager of its
/// own, which only reaches its own connections, so without this a peer on <see cref="Constants.ParallelRelayPath"/> would
/// never be sent a <c>Receive</c>, <c>Peers</c> or <c>Kicked</c> from anything addressing it through <see cref="StyxHub"/>.
/// </summary>
public sealed class SharedHubLifetimeManager<THub>(HubLifetimeManager<StyxHub> shared) : HubLifetimeManager<THub> where THub : StyxHub
{
    public override Task OnConnectedAsync(HubConnectionContext connection) => shared.OnConnectedAsync(connection);

    public override Task OnDisconnectedAsync(HubConnectionContext connection) => shared.OnDisconnectedAsync(connection);

    public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        shared.SendAllAsync(methodName, args, cancellationToken);

    public override Task SendAllExceptAsync(string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds,
        CancellationToken cancellationToken = default) => shared.SendAllExceptAsync(methodName, args, excludedConnectionIds, cancellationToken);

    public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        shared.SendConnectionAsync(connectionId, methodName, args, cancellationToken);

    public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args,
        CancellationToken cancellationToken = default) => shared.SendConnectionsAsync(connectionIds, methodName, args, cancellationToken);

    public override Task SendGroupAsync(string groupName, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        shared.SendGroupAsync(groupName, methodName, args, cancellationToken);

    public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args,
        CancellationToken cancellationToken = default) => shared.SendGroupsAsync(groupNames, methodName, args, cancellationToken);

    public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds,
        CancellationToken cancellationToken = default) => shared.SendGroupExceptAsync(groupName, methodName, args, excludedConnectionIds, cancellationToken);

    public override Task SendUserAsync(string userId, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        shared.SendUserAsync(userId, methodName, args, cancellationToken);

    public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        shared.SendUsersAsync(userIds, methodName, args, cancellationToken);

    public override Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        shared.AddToGroupAsync(connectionId, groupName, cancellationToken);

    public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        shared.RemoveFromGroupAsync(connectionId, groupName, cancellationToken);

    public override Task<T> InvokeConnectionAsync<T>(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken) =>
        shared.InvokeConnectionAsync<T>(connectionId, methodName, args, cancellationToken);

    public override Task SetConnectionResultAsync(string connectionId, CompletionMessage result) => shared.SetConnectionResultAsync(connectionId, result);

    public override bool TryGetReturnType(string invocationId, [NotNullWhen(true)] out Type? type) => shared.TryGetReturnType(invocationId, out type);
}
