/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using Altruist.Contracts;

using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Redis;

public sealed class RedisServiceConfiguration : ICacheConfiguration
{
    public bool IsConfigured { get; set; }

    public readonly List<Type> Documents = new List<Type>();
    public void Configure(IServiceCollection services)
    {
        // services.AddSingleton<RedisConnectionSetup>();
        // services.AddSingleton<ICacheConnectionSetupBase>(sp => sp.GetRequiredService<RedisConnectionSetup>());
    }

    public void AddDocument<T>() where T : IStoredModel
    {
        AddDocument(typeof(T));
    }

    public void AddDocument(Type type)
    {
        if (typeof(IStoredModel).IsAssignableFrom(type))
        {
            Documents.Add(type);
        }
    }

    Task IAltruistConfiguration.Configure(IServiceCollection services)
    {
        throw new NotImplementedException();
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

    public string Description => "💾 Cache: Redis";
}
