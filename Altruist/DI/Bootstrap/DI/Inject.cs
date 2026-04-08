using Microsoft.Extensions.DependencyInjection;

namespace Altruist;

public static class Dependencies
{
    private static IServiceProvider? _provider;
    private static IServiceCollection? _services;

    public static IServiceProvider? RootProvider => _provider;

    /// <summary>
    /// Set the root service provider. Call this once during bootstrap.
    /// </summary>
    public static void UseRootProvider(IServiceProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>
    /// Set the service collection for fallback resolution before provider is built.
    /// </summary>
    public static void UseServices(IServiceCollection services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>
    /// Resolve a service from the global root provider.
    /// </summary>
    public static T Inject<T>() where T : notnull
    {
        if (_provider is null)
        {
            if (_services is null)
                throw new InvalidOperationException("No service provider or service collection configured. Call AltruistDI.Run() first.");
            var tmpProvider = _services.BuildServiceProvider();
            return tmpProvider.GetRequiredService<T>();
        }

        return _provider.GetRequiredService<T>();
    }

    /// <summary>
    /// Non-generic resolve if you ever need it.
    /// </summary>
    public static object Inject(Type serviceType)
    {
        if (serviceType is null)
            throw new ArgumentNullException(nameof(serviceType));

        if (_provider is null)
        {
            if (_services is null)
                throw new InvalidOperationException("No service provider or service collection configured. Call AltruistDI.Run() first.");
            var tmpProvider = _services.BuildServiceProvider();
            return tmpProvider.GetRequiredService(serviceType);
        }

        return _provider.GetRequiredService(serviceType);
    }
}
