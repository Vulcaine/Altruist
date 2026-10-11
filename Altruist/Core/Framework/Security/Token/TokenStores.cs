/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Contracts;
using Altruist.Persistence;
using Altruist.UORM;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Altruist.Security;

/// <summary>
/// Registers the SQL token stores for the token vaults the application declares: an
/// <see cref="IRefreshTokenService{TModel}"/> for each <see cref="RefreshTokenModel"/> vault and an
/// <see cref="IOneTimeTokenStore{TModel}"/> for each <see cref="OneTimeTokenModel"/> vault (also as
/// <see cref="IRefreshTokenService"/> / <see cref="IOneTimeTokenStore"/> when there is exactly one of
/// the kind), and the <see cref="TokenPruneService"/> that cleans them up. Nothing is registered
/// for applications without such vaults.
/// </summary>
[ServiceConfiguration]
public sealed class TokenStoreConfiguration : IAltruistConfiguration
{
    /// <summary>True once <see cref="Configure"/> ran.</summary>
    public bool IsConfigured { get; set; }

    /// <summary>Scans the loaded assemblies for <c>[Vault]</c> types and registers the token stores (see <see cref="Register"/>). Called by Altruist at start-up.</summary>
    public Task Configure(IServiceCollection services)
    {
        var models = TypeDiscovery.FindTypesWithAttribute<VaultAttribute>(
                AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName)))
            .ToList();
        Register(services, models);
        IsConfigured = true;
        return Task.CompletedTask;
    }

    /// <summary>Registers the stores for the token vaults among <paramref name="vaultTypes"/>.</summary>
    public static void Register(IServiceCollection services, IEnumerable<Type> vaultTypes)
    {
        var types = vaultTypes.Distinct().Where(t => !t.IsAbstract).ToList();
        var refresh = types.Where(t => typeof(RefreshTokenModel).IsAssignableFrom(t)).ToList();
        var oneTime = types.Where(t => typeof(OneTimeTokenModel).IsAssignableFrom(t)).ToList();
        var config = AppConfigLoader.Load();

        // Stores resolve the SQL provider on first use: applications without SQL that never ask
        // for a store are unaffected.
        foreach (var model in refresh)
        {
            var storeType = typeof(RefreshTokenService<>).MakeGenericType(model);
            var serviceType = typeof(IRefreshTokenService<>).MakeGenericType(model);
            services.AddSingleton(serviceType, sp => Activator.CreateInstance(storeType,
                sp.GetRequiredService<ISqlDatabaseProvider>(), RefreshTokenOptions.FromConfiguration(config), sp.GetService<ILoggerFactory>(), null)!);
            services.AddSingleton<ITokenPruner>(sp => (ITokenPruner)sp.GetRequiredService(serviceType));
            if (refresh.Count == 1)
                services.AddSingleton<IRefreshTokenService>(sp => (IRefreshTokenService)sp.GetRequiredService(serviceType));
        }

        foreach (var model in oneTime)
        {
            var storeType = typeof(OneTimeTokenStore<>).MakeGenericType(model);
            var serviceType = typeof(IOneTimeTokenStore<>).MakeGenericType(model);
            services.AddSingleton(serviceType, sp => Activator.CreateInstance(storeType,
                sp.GetRequiredService<ISqlDatabaseProvider>(), OneTimeTokenOptions.FromConfiguration(config), null)!);
            services.AddSingleton<ITokenPruner>(sp => (ITokenPruner)sp.GetRequiredService(serviceType));
            if (oneTime.Count == 1)
                services.AddSingleton<IOneTimeTokenStore>(sp => (IOneTimeTokenStore)sp.GetRequiredService(serviceType));
        }

        if (refresh.Count + oneTime.Count > 0)
            services.AddSingleton<IHostedService, TokenPruneService>();
    }
}

/// <summary>
/// Deletes stale tokens of every <see cref="ITokenPruner"/> at start-up and then every
/// <c>altruist:security:token-prune:interval-minutes</c> (default 60; 0 turns it off).
/// Failures are logged and retried at the next interval.
/// </summary>
public sealed class TokenPruneService : IHostedService
{
    /// <summary>Config key of the prune interval, in minutes.</summary>
    public const string IntervalKey = "altruist:security:token-prune:interval-minutes";

    private readonly Func<IEnumerable<ITokenPruner>> _pruners;
    private readonly TimeSpan _interval;
    private readonly ILogger _log;
    private CancellationTokenSource? _stop;
    private Task? _loop;

    /// <summary>DI constructor: prunes every registered <see cref="ITokenPruner"/> at the interval from <see cref="IntervalKey"/> (default 60 minutes).</summary>
    [ActivatorUtilitiesConstructor]
    public TokenPruneService(IServiceProvider services, ILoggerFactory loggerFactory)
        : this(services.GetServices<ITokenPruner>, loggerFactory, TimeSpan.FromMinutes(
            TokenConfig.Number(AppConfigLoader.Load().GetSection("altruist:security:token-prune"), "interval-minutes") ?? 60))
    { }

    /// <summary>Creates the service with explicit pruners and interval (tests).</summary>
    /// <param name="pruners">Resolved at every run, so a store that cannot be built yet (database down) is retried.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="interval">Time between runs; zero or negative disables pruning.</param>
    public TokenPruneService(Func<IEnumerable<ITokenPruner>> pruners, ILoggerFactory loggerFactory, TimeSpan interval)
    {
        _pruners = pruners;
        _interval = interval;
        _log = loggerFactory.CreateLogger<TokenPruneService>();
    }

    /// <summary>Starts a background loop that prunes immediately and then every interval (does nothing when the interval is not positive).</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_interval <= TimeSpan.Zero)
            return Task.CompletedTask;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_interval);
            do
            {
                await PruneOnceAsync(token).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }, token);
        return Task.CompletedTask;
    }

    /// <summary>Runs every pruner once; returns the number of rows removed.</summary>
    public async Task<long> PruneOnceAsync(CancellationToken ct = default)
    {
        long total = 0;
        IEnumerable<ITokenPruner> pruners;
        try
        {
            pruners = _pruners().ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Token stores could not be created; pruning skipped");
            return 0;
        }
        foreach (var pruner in pruners)
        {
            try
            {
                var removed = await pruner.PruneAsync(DateTime.UtcNow, ct).ConfigureAwait(false);
                if (removed > 0)
                    _log.LogInformation("Pruned {Count} {What}", removed, pruner.PrunedName);
                total += removed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Pruning {What} failed", pruner.PrunedName);
            }
        }
        return total;
    }

    /// <summary>Stops the loop and waits for a run in progress.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stop?.Cancel();
        if (_loop is not null)
        {
            try
            { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }
}
