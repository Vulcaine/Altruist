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

using Altruist.Engine;
using Altruist.Physx;
using Altruist.Physx.ThreeD;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Engine;

public enum EngineMode
{
    TwoD,
    ThreeD
}

// public class AltruistGameEngineBuilder
// {
//     private IServiceCollection Services { get; }
//     private IAltruistContext Settings;
//     private string[] _args;

//     public AltruistGameEngineBuilder(IServiceCollection services, IAltruistContext Settings, string[] args)
//     {
//         Services = services;
//         this.Settings = Settings;
//         _args = args;
//     }

//     // public AltruistConnectionBuilder NoEngine()
//     // {
//     //     return new AltruistConnectionBuilder(Services, Settings, _args);
//     // }

//     public AltruistGameEngineBuilder AddWorld(IWorldIndex worldIndex)
//     {
//         Services.AddKeyedSingleton(typeof(IWorldIndex), $"world-{worldIndex.Index()}", worldIndex);
//         return this;
//     }

//     public AltruistConnectionBuilder EnableEngine(int hz, CycleUnit unit = CycleUnit.Ticks, int? throttle = null, EngineMode mode = EngineMode.TwoD)
//     {
//         // Services.AddSingleton<IAction, EngineStartupConfiguration>();
//         // Services.AddSingleton<IAltruistEngineRouter, InMemoryEngineRouter>();
//         // Services.AddSingleton<EngineClientSender>();
//         // Services.AddSingleton<IAltruistEngine>(sp =>
//         // {
//         //     var coordinator = sp.GetRequiredService<IGameWorldCoordinator>();

//         //     var env = sp.GetRequiredService<IHostEnvironment>();
//         //     var engine = new AltruistEngine(sp, coordinator, hz, unit, throttle);

//         //     if (env.IsDevelopment())
//         //     {
//         //         return new EngineWithDiagnostics(engine, sp.GetRequiredService<ILoggerFactory>());
//         //     }
//         //     else
//         //     {
//         //         return engine;
//         //     }
//         // });

//         // Services.AddSingleton<IAltruistRouter>(sp => sp.GetRequiredService<IAltruistEngineRouter>());
//         // Services.AddSingleton<MethodScheduler>();
//         Settings.EngineEnabled = true;
//         return new AltruistConnectionBuilder(Services, Settings, _args);
//     }
// }
