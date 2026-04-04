// /* 
// Copyright 2025 Aron Gere

// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at

//     http://www.apache.org/licenses/LICENSE-2.0

// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// */

// using Altruist.Contracts;
// using Altruist.Persistence;
// using Microsoft.Extensions.DependencyInjection;
// using Microsoft.Extensions.Logging;

// namespace Altruist;

// [Configuration]
// public class LoadSyncServicesAction : IAltruistConfiguration
// {
//     public async Task Configure(IServiceCollection services)
//     {
//         var serviceProvider = services.BuildServiceProvider();
//         var logger = serviceProvider.GetRequiredService<ILogger<LoadSyncServicesAction>>();
//         var syncServices = serviceProvider.GetServices<ISyncService>().ToList();

//         if (syncServices.Count == 0)
//         {
//             logger.LogInformation("ℹ️  No sync services found to load.");
//             return;
//         }

//         logger.LogInformation($"🔄 Starting cache sync for **{syncServices.Count}** service(s)...");

//         var completed = new List<Type>();
//         var failed = new List<(Type Type, string Error)>();

//         for (int i = 0; i < syncServices.Count; i++)
//         {
//             var service = syncServices[i];
//             var serviceType = service.GetType();

//             try
//             {
//                 logger.LogInformation($"🔁 [{i + 1}/{syncServices.Count}] Syncing `{serviceType.Name}`...");
//                 await service.PullAsync();

//                 completed.Add(serviceType);
//                 var pct = completed.Count * 100 / syncServices.Count;
//                 logger.LogInformation($"✅ `{serviceType.Name}` synced successfully. ({pct}%)");
//             }
//             catch (Exception ex)
//             {
//                 logger.LogError(ex, $"❌ `{serviceType.Name}` failed to sync.");
//                 failed.Add((serviceType, ex.Message));
//             }
//         }

//         if (failed.Count == 0)
//         {
//             logger.LogInformation("🎉 All sync services completed successfully!");
//         }
//         else
//         {
//             logger.LogWarning($"⚠️ Sync completed with **{failed.Count}** failure(s) out of **{syncServices.Count}** services.");
//             logger.LogWarning("📋 Failed services:");

//             foreach (var (type, error) in failed)
//             {
//                 logger.LogWarning($"   • ❌ `{type.Name}` → {error}");
//             }
//         }
//     }
// }
