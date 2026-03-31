using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Styx.Services;

namespace Styx.Filters;

[AttributeUsage(AttributeTargets.Method)]
public class AllowAnonymousHubAttribute : Attribute { }

public class AuthenticationHubFilter(IClientRegistry registry) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext context,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (context.HubMethod.GetCustomAttribute<AllowAnonymousHubAttribute>() != null)
            return await next(context);

        var identity = await registry.GetIdentity(context.Context.ConnectionId);
        if (identity != null)
            return await next(context);

        context.Context.Abort();
        throw new HubException("Not authenticated");
    }
}
