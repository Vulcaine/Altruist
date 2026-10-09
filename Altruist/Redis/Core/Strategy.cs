/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist.Contracts;

using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Redis;

/// <summary>
/// Configuration of the Redis cache: the list of model types (<see cref="IStoredModel"/>) the cache
/// can store. Reached through <c>RedisCacheServiceToken.Instance.Configuration</c>.
/// </summary>
/// <remarks>
/// <see cref="RedisCacheProvider"/> reads <see cref="Documents"/> once, in its constructor; a type
/// added afterwards is not known to that provider instance and its typed calls throw
/// <see cref="KeyNotFoundException"/>. Not thread-safe; populate it during startup.
/// </remarks>
public sealed class RedisServiceConfiguration : ICacheConfiguration
{
    /// <inheritdoc/>
    public bool IsConfigured { get; set; }

    /// <summary>Model types registered for the Redis cache (no duplicates).</summary>
    public readonly List<Type> Documents = new List<Type>();

    /// <summary>
    /// On first call, scans every loaded non-dynamic assembly and registers each concrete
    /// <see cref="IStoredModel"/> type via <see cref="AddDocument(Type)"/>; assemblies that fail to load
    /// their types are skipped. Later calls do nothing. Does not touch <paramref name="services"/>.
    /// </summary>
    /// <param name="services">Unused.</param>
    public Task Configure(IServiceCollection services)
    {
        // Auto-discover all IStoredModel types for Redis document mapping
        if (!IsConfigured)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName));

            foreach (var assembly in assemblies)
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (!type.IsAbstract && !type.IsInterface &&
                            typeof(IStoredModel).IsAssignableFrom(type))
                        {
                            AddDocument(type);
                        }
                    }
                }
                catch (ReflectionTypeLoadException)
                {
                    // Skip assemblies that can't be loaded
                }
            }

            IsConfigured = true;
        }

        return Task.CompletedTask;
    }

    /// <summary>Registers <typeparamref name="T"/> as a cacheable document type (ignored if already present).</summary>
    /// <typeparam name="T">A stored model type.</typeparam>
    public void AddDocument<T>() where T : IStoredModel
    {
        AddDocument(typeof(T));
    }

    /// <summary>Registers <paramref name="type"/> when it implements <see cref="IStoredModel"/> and is not yet present; other types are silently ignored.</summary>
    /// <param name="type">The model type.</param>
    public void AddDocument(Type type)
    {
        if (typeof(IStoredModel).IsAssignableFrom(type) && !Documents.Contains(type))
        {
            Documents.Add(type);
        }
    }
}

/// <summary>
/// Identifies Redis as the cache backend (<c>altruist:persistence:cache:provider</c> = <c>redis</c>)
/// and carries its <see cref="RedisServiceConfiguration"/>. Use the process-wide <see cref="Instance"/>;
/// the alternative backend's token is <c>InMemoryCacheServiceToken</c> (<c>inmemory</c>).
/// </summary>
[Service(typeof(ICacheServiceToken))]
[ConditionalOnConfig("altruist:persistence:cache:provider", havingValue: "redis")]
public sealed class RedisCacheServiceToken : ICacheServiceToken
{
    /// <summary>The single instance (its <see cref="Configuration"/> is shared process-wide).</summary>
    public static readonly RedisCacheServiceToken Instance = new();
    /// <summary>The Redis cache configuration; always a <see cref="RedisServiceConfiguration"/>.</summary>
    public ICacheConfiguration Configuration { get; }

    private RedisCacheServiceToken()
    {
        Configuration = new RedisServiceConfiguration();
    }

    /// <summary>Startup banner text: <c>"Cache: Redis"</c>.</summary>
    public string Description => "Cache: Redis";
}
