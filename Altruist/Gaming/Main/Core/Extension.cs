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

using System.Runtime.CompilerServices;
using Altruist;
using Altruist.Contracts;
using Altruist.Gaming;
using Altruist.Gaming.Engine;
using Altruist.Physx;
using Microsoft.Extensions.DependencyInjection;

public static class AltruistGamingServiceCollectionExtensions
{
    public static AltruistConnectionBuilder SetupGameEngine(this AltruistIntermediateBuilder builder, Func<AltruistGameEngineBuilder, AltruistConnectionBuilder> setup)
    {
        // builder.Services.AddGamingSupport();
        var engineBuilder = new AltruistGameEngineBuilder(builder.Services, builder.Settings, builder.Args);
        return setup.Invoke(engineBuilder);
    }
}


public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        ServiceConfig.Register(new GameConfig());
    }
}

public class GameConfig : IConfiguration
{
    public void Configure(IServiceCollection services)
    {
        // services.AddSingleton<GamePortalContext>();
        services.AddSingleton<IWorldPartitioner>(sp =>
       {
           return new WorldPartitioner(64, 64);
       });

        // services.AddSingleton<IPlayerCursorFactory, PlayerCursorFactory>();
        services.AddSingleton(typeof(PlayerCursor<>));
        // services.AddSingleton<GameWorldCoordinator>();
        services.AddSingleton<MovementPhysx>();
        services.AddSingleton(typeof(IPlayerService<>), typeof(AltruistPlayerService<>));
    }
}