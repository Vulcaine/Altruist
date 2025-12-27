using Altruist;

using Microsoft.Extensions.DependencyInjection;

public static class Dependencies
{
    private static IServiceProvider? _provider;

    public static IServiceProvider? RootProvider => _provider;

    /// <summary>
    /// Set the root service provider. Call this once during bootstrap.
    /// </summary>
    public static void UseRootProvider(IServiceProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>
    /// Resolve a service from the global root provider.
    /// </summary>
    public static T Inject<T>() where T : notnull
    {
        if (_provider is null)
            throw new InvalidOperationException(
                "Altruist root provider is not initialized. " +
                "Call Altruist.UseRootProvider(...) during bootstrap.");

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
            var tmpProvider = AltruistBootstrap.Services.BuildServiceProvider();
            return tmpProvider.GetRequiredService(serviceType);
        }

        return _provider.GetRequiredService(serviceType);
    }
}
