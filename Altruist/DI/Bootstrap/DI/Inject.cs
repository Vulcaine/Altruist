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
    /// Fails while bootstrap is still registering services (before the root provider exists): a provider built from the
    /// unfinished collection would hand out singletons that are not the instances the app uses.
    /// </remarks>
    /// <typeparam name="T">Service type.</typeparam>
    /// <exception cref="InvalidOperationException">The root provider is not built yet, or <typeparamref name="T"/> is not registered.</exception>
    public static T Inject<T>() where T : notnull
        => ProviderOrFail().GetRequiredService<T>();

    /// <summary>
    /// Non-generic resolve if you ever need it. Same lookup rules as <see cref="Inject{T}"/>.
    /// </summary>
    /// <param name="serviceType">Service type to resolve.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serviceType"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The root provider is not built yet, or the type is not registered.</exception>
    public static object Inject(Type serviceType)
    {
        if (serviceType is null)
            throw new ArgumentNullException(nameof(serviceType));

        return ProviderOrFail().GetRequiredService(serviceType);
    }

    private static IServiceProvider ProviderOrFail() =>
        _scope.Value ?? _provider ?? throw new InvalidOperationException(
            "Dependencies.Inject was called before the root service provider was built (during bootstrap). " +
            "Take the dependency through the constructor, or resolve it in a [PostConstruct] hook.");

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
