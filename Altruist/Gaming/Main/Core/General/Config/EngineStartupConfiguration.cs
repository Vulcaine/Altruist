
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

using System.Reflection;

using Altruist.Contracts;
using Altruist.Engine;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Engine;

[AppConfiguration]
public class EngineStartupConfiguration : IAltruistConfiguration
{
    public async Task Configure(IServiceCollection services)
    {
        var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILogger<EngineStartupConfiguration>>();
        var settings = serviceProvider.GetRequiredService<IAltruistContext>();
        if (settings.EngineEnabled)
        {
            logger.LogInformation("🚀 Starting engine...");
            var scheduler = serviceProvider.GetRequiredService<MethodScheduler>();
            var methods = scheduler!.RegisterMethods(serviceProvider);
            var engine = serviceProvider.GetRequiredService<IAltruistEngine>();
            engine!.Start();
            logger.LogInformation($"⚡⚡ [ENGINE {engine.Rate}Hz] Unleashed — powerful, fast, and breaking speed limits!");

            if (methods.Any())
            {
                var methodsDisplay = string.Join("\n", methods.Select(m =>
                {
                    var regen = m.GetCustomAttribute<CycleAttribute>();
                    var frequency = regen!.ToString();
                    return $"       ↳ {m.DeclaringType?.FullName!.Split('`')[0]}.{m.Name} ({frequency})";
                }));

                logger.LogInformation($"   🚀 Scheduled methods:\n{methodsDisplay}");
            }
            else
            {
                logger.LogInformation("❗Nothing to run.. 🙁 Mark something with [Regen(Hz or cron)] to let me show my power. Please!");
            }
        }
    }
}