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
using Altruist.Web.Features;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist
{
    public class AltruistServiceConfig : IAltruistConfiguration
    {

        public Task Configure(IServiceCollection services)
        {
            var cfg = GetConfig();
            var logger = GetLogger(services);

            // one-time converter discovery for config binding
            DependencyResolver.EnsureConverters(services, logger);

            var registered = new List<string>();
            RegisterServiceAttributes(services, cfg, logger, registered);
            RegisterPortals(services, cfg, logger, registered);

            LogRegistered(logger, registered);
            return Task.CompletedTask;
        }

        // ---------- High-level helpers ----------

        private static IConfiguration GetConfig() => AppConfigLoader.Load();

        private static ILogger GetLogger(IServiceCollection services) =>
            services.BuildServiceProvider().GetRequiredService<ILoggerFactory>().CreateLogger<AltruistServiceConfig>();

        private static Assembly[] GetAssemblies() =>
            AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName)).ToArray();

        private static IEnumerable<Type> Find<TAttr>() where TAttr : Attribute =>
            TypeDiscovery.FindTypesWithAttribute<TAttr>(GetAssemblies());

        private static void LogRegistered(ILogger logger, List<string> reg)
        { if (reg.Count > 0) logger.LogDebug("✅ Registered services:\n{Services}", string.Join("\n", reg)); }

        // ---------- Service & Portal registration ----------

        private static void RegisterServiceAttributes(IServiceCollection services, IConfiguration cfg, ILogger log, List<string> reg)
        {
            foreach (var implType in Find<ServiceAttribute>())
                RegisterServiceType(services, cfg, log, reg, implType);
        }

        private static void RegisterServiceType(IServiceCollection services, IConfiguration cfg, ILogger log, List<string> reg, Type implType)
        {
            if (!DependencyResolver.ShouldRegister(implType, cfg, log))
                return;

            foreach (var svcAttr in implType.GetCustomAttributes<ServiceAttribute>())
            {
                var svcType = svcAttr.ServiceType ?? DependencyResolver.InferServiceType(implType);
                services.Add(new ServiceDescriptor(
                    svcType,
                    sp =>
                    {
                        var obj = DependencyResolver.CreateWithConfiguration(sp, cfg, implType, log, svcAttr.Lifetime);
                        try
                        {
                            _ = DependencyResolver.InvokePostConstructAsync(obj, sp, cfg, log);
                        }
                        catch (Exception ex)
                        {
                            log.LogError(ex, "❌ PostConstruct failed on service {Type}.", implType.FullName);
                            throw;
                        }
                        return obj!;
                    },
                    svcAttr.Lifetime));

                reg.Add($"\t{DependencyResolver.GetCleanName(svcType)} → {DependencyResolver.GetCleanName(implType)} ({svcAttr.Lifetime})");
            }
        }

        private void RegisterPortals(IServiceCollection services, IConfiguration cfg, ILogger log, List<string> reg)
        {
            var provider = services.BuildServiceProvider();
            IAltruistContext _settings = provider.GetRequiredService<IAltruistContext>();
            var portals = PortalDiscovery.Discover().Distinct();
            foreach (var t in portals)
            {
                if (!t.PortalType.IsClass || t.PortalType.IsAbstract)
                    continue;
                if (!DependencyResolver.ShouldRegister(t.PortalType, cfg, log))
                    continue;

                services.Add(new ServiceDescriptor(
                    t.PortalType,
                    sp =>
                    {
                        var obj = DependencyResolver.CreateWithConfiguration(sp, cfg, t.PortalType, log);
                        try
                        {
                            _ = DependencyResolver.InvokePostConstructAsync(obj, sp, cfg, log);
                        }
                        catch (Exception ex)
                        {
                            log.LogError(ex, "❌ PostConstruct failed on portal {Type}.", t.PortalType.FullName);
                            throw;
                        }
                        return obj!;
                    },
                    ServiceLifetime.Transient));

                _settings.AddEndpoint(t.Path);

                reg.Add($"\t{DependencyResolver.GetCleanName(t.PortalType)} → {DependencyResolver.GetCleanName(t.PortalType)} (Transient) [Portal]");
            }
        }
    }
}
