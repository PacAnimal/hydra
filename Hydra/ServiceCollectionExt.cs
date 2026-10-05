using System.Diagnostics.CodeAnalysis;
using Hydra.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra;

internal static class ServiceCollectionExt
{
    /// <summary>
    /// One instance, resolvable as itself AND run as a hosted service. <c>AddHostedService&lt;T&gt;()</c> on its
    /// own would construct a second instance that nothing else can reach.
    /// </summary>
    internal static IServiceCollection AddHostedSingleton<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        this IServiceCollection services) where T : class, IHostedService
    {
        services.AddSingleton<T>();
        return services.AddHostedService(sp => sp.GetRequiredService<T>());
    }

    internal static IServiceCollection AddHostedSingleton<T>(this IServiceCollection services, Func<IServiceProvider, T> factory)
        where T : class, IHostedService
    {
        services.AddSingleton(factory);
        return services.AddHostedService(sp => sp.GetRequiredService<T>());
    }

    internal static IServiceCollection AddHostedSingleton<T>(this IServiceCollection services, T instance) where T : class, IHostedService
    {
        services.AddSingleton(instance);
        return services.AddHostedService(_ => instance);
    }

    // a slave's output handler, injecting through the coalescing wrapper and serving as the cursor
    internal static IServiceCollection AddCoalescedOutput<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
        this IServiceCollection services, Action<THandler>? initialize = null) where THandler : class, IPlatformOutput, ICursor
    {
        services.AddSingleton<THandler>();
        services.AddSingleton<IPlatformOutput>(sp =>
        {
            var handler = sp.GetRequiredService<THandler>();
            initialize?.Invoke(handler);
            return new CoalescingOutputWrapper(handler, sp.GetRequiredService<ILogger<CoalescingOutputWrapper>>());
        });
        return services.AddSingleton<ICursor>(sp => sp.GetRequiredService<THandler>());
    }
}
