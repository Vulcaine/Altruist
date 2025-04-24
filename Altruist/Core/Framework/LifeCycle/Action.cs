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

using Altruist.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist;

public interface IAction
{
    Task Run();
}

public abstract class ActionBase : IAction
{
    protected IServiceProvider ServiceProvider { get; private set; } = default!;
    protected ILogger Logger { get; private set; } = default!;

    public ActionBase(IServiceProvider serviceProvider)
    {
        ServiceProvider = serviceProvider;
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        Logger = loggerFactory.CreateLogger(GetType());
    }

    public abstract Task Run();
}

public class LoadSyncServicesAction : ActionBase
{
    public LoadSyncServicesAction(IServiceProvider serviceProvider) : base(serviceProvider)
    {
    }

    public override async Task Run()
    {
        var syncServices = ServiceProvider.GetServices<object>()
            .Where(s => s?.GetType().GetInterfaces()
                .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IVaultCacheSyncService<>)) == true)
            .ToList();

        if (syncServices.Count == 0)
        {
            Logger.LogInformation("ℹ️  No vault cache sync services found to load.");
            return;
        }

        Logger.LogInformation($"🔄 Starting cache sync for **{syncServices.Count}** vault service(s)...");

        var completed = new List<Type>();
        var failed = new List<(Type Type, string Error)>();

        int index = 1;
        foreach (var service in syncServices)
        {
            var serviceType = service.GetType();
            try
            {
                var syncInterface = serviceType.GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IVaultCacheSyncService<>));

                if (syncInterface != null)
                {
                    var loadMethod = syncInterface.GetMethod("Load");
                    if (loadMethod != null)
                    {
                        Logger.LogInformation($"🔁 [{index}/{syncServices.Count}] Syncing `{serviceType.Name}`...");
                        var task = (Task)loadMethod.Invoke(service, null)!;
                        await task;

                        completed.Add(serviceType);
                        var pct = completed.Count * 100 / syncServices.Count;
                        Logger.LogInformation($"✅ `{serviceType.Name}` synced successfully. ({pct}%)");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"❌ `{serviceType.Name}` failed to sync.");
                failed.Add((serviceType, ex.Message));
            }

            index++;
        }

        // Final summary
        if (failed.Count == 0)
        {
            Logger.LogInformation("🎉 All vault cache services synced successfully!");
        }
        else
        {
            Logger.LogWarning($"⚠️ Cache sync completed with **{failed.Count}** failure(s) out of **{syncServices.Count}** services.");
            Logger.LogWarning("📋 Failed services:");

            foreach (var fail in failed)
            {
                Logger.LogWarning($"   • ❌ `{fail.Type.Name}` → {fail.Error}");
            }
        }
    }
}
