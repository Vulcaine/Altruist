/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist.Contracts;

using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Redis;

public sealed class RedisServiceConfiguration : ICacheConfiguration
{
    public bool IsConfigured { get; set; }

    public readonly List<Type> Documents = new List<Type>();

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

    public void AddDocument<T>() where T : IStoredModel
    {
        AddDocument(typeof(T));
    }

    public void AddDocument(Type type)
    {
        if (typeof(IStoredModel).IsAssignableFrom(type) && !Documents.Contains(type))
        {
            Documents.Add(type);
        }
    }
}

[Service(typeof(ICacheServiceToken))]
[ConditionalOnConfig("altruist:persistence:cache:provider", havingValue: "redis")]
public sealed class RedisCacheServiceToken : ICacheServiceToken
{
    public static readonly RedisCacheServiceToken Instance = new();
    public ICacheConfiguration Configuration { get; }

    private RedisCacheServiceToken()
    {
        Configuration = new RedisServiceConfiguration();
    }

    public string Description => "Cache: Redis";
}
