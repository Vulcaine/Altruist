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

namespace Altruist.Redis;

public static class Extensions
{
    public static AltruistDatabaseBuilder WithRedis(this IAfterConnectionBuilder builder)
    {
        return builder.SetupCache<RedisConnectionSetup>(RedisCacheServiceToken.Instance);
    }

    public static AltruistDatabaseBuilder WithRedis(this IAfterConnectionBuilder builder, Func<RedisConnectionSetup, RedisConnectionSetup>? setup)
    {
        return builder.SetupCache(RedisCacheServiceToken.Instance, setup);
    }

    public static RedisConnectionSetup ForgeDocuments(this RedisConnectionSetup setup)
    {
        var modelTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IStoredModel).IsAssignableFrom(t) && !t.IsAbstract && t.IsClass);

        foreach (var type in modelTypes)
        {
            setup.AddDocument(type);
        }

        return setup;
    }

}