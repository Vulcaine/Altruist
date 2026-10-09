using Microsoft.Extensions.DependencyInjection;

namespace Altruist;

/// <summary>
/// Static service locator over the root provider built at bootstrap. Use it only where constructor injection is impossible
/// (static helpers, objects created by third-party code, attribute-instantiated types); everywhere else take dependencies
/// through the constructor of a <see cref="ServiceAttribute"/> class.
/// </summary>
/// <example>
/// <code>
/// var clock = Dependencies.Inject&lt;IClock&gt;();
/// </code>
/// </example>
public static class Dependencies
{
    private static IServiceProvider? _provider;
    private static IServiceCollection? _services;

    /// <summary>
    /// Per-async-flow override for <see cref="Inject{T}"/> resolutions. Test
    /// harnesses push a per-test provider here so production code paths that call
    /// <c>Dependencies.Inject&lt;T&gt;</c> (e.g. PgVaultQuery) resolve from the
    /// per-test container instead of the process-wide root.
    /// </summary>
    private static readonly AsyncLocal<IServiceProvider?> _scope = new();

    /// <summary>The root provider set by <see cref="UseRootProvider"/>; null before bootstrap finishes building it.</summary>
    public static IServiceProvider? RootProvider => _provider;

    /// <summary>The provider in effect for the current async flow — the scoped
    /// provider if one is pushed, otherwise the root.</summary>
    public static IServiceProvider? CurrentProvider => _scope.Value ?? _provider;

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
    /// Override the active provider for the current async flow only. Returns an
    /// <see cref="IDisposable"/> that restores the previous scope on dispose.
    /// Designed for test harnesses; production code should not call this.
    /// </summary>
    public static IDisposable PushScope(IServiceProvider provider)
    {
        if (provider is null) throw new ArgumentNullException(nameof(provider));
        var previous = _scope.Value;
        _scope.Value = provider;
        return new ScopePopper(previous);
    }

    /// <summary>
    /// Resolve a service. Honors any active <see cref="PushScope"/> before falling
    /// back to the root provider, so test-time overrides take precedence.
    /// </summary>
    /// <remarks>
    /// Before the root provider exists (during bootstrap) it builds a throw-away provider from the registered
    /// collection on every call, so singletons obtained that way are NOT the instances the app later uses.
    /// </remarks>
    /// <typeparam name="T">Service type.</typeparam>
    /// <exception cref="InvalidOperationException">Nothing configured yet, or <typeparamref name="T"/> is not registered.</exception>
    public static T Inject<T>() where T : notnull
    {
        var sp = _scope.Value ?? _provider;
        if (sp is not null)
            return sp.GetRequiredService<T>();

        if (_services is null)
            throw new InvalidOperationException("No service provider or service collection configured. Call AltruistDI.Run() first.");

        var tmpProvider = _services.BuildServiceProvider();
        return tmpProvider.GetRequiredService<T>();
    }

    /// <summary>
    /// Non-generic resolve if you ever need it. Same lookup rules as <see cref="Inject{T}"/>.
    /// </summary>
    /// <param name="serviceType">Service type to resolve.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serviceType"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Nothing configured yet, or the type is not registered.</exception>
    public static object Inject(Type serviceType)
    {
        if (serviceType is null)
            throw new ArgumentNullException(nameof(serviceType));

        var sp = _scope.Value ?? _provider;
        if (sp is not null)
            return sp.GetRequiredService(serviceType);

        if (_services is null)
            throw new InvalidOperationException("No service provider or service collection configured. Call AltruistDI.Run() first.");

        var tmpProvider = _services.BuildServiceProvider();
        return tmpProvider.GetRequiredService(serviceType);
    }

    private sealed class ScopePopper : IDisposable
    {
        private readonly IServiceProvider? _previous;
        private int _disposed;
        public ScopePopper(IServiceProvider? previous) => _previous = previous;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _scope.Value = _previous;
        }
    }
}
