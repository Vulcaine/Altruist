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

using Altruist;
using Altruist.Gaming;
using Altruist.Gaming.Engine;
using Microsoft.Extensions.DependencyInjection;

public static class AltruistGamingServiceCollectionExtensions
{
    public static IServiceCollection AddGamingSupport(this IServiceCollection services)
    {
        services.AddSingleton<IWorldPartitioner>(sp =>
        {
            return new WorldPartitioner(64, 64);
        });
        services.AddSingleton<GameWorldCoordinator>();
        services.AddSingleton(typeof(IPlayerService<>), typeof(AltruistPlayerService<>));
        return services;
    }

    public static AltruistEngineBuilder GameEngine(this AltruistIntermediateBuilder builder) => new AltruistEngineBuilder(builder.Services, builder.Settings, builder.Args);
}
