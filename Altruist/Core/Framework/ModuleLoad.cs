using System.Reflection;
using System.Runtime.CompilerServices;
using Altruist.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist;

public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        ServiceConfig.Register(new AltruistConfig());
    }
}

public class AltruistConfig : IConfiguration
{
    public void Configure(IServiceCollection services)
    {
        var logger = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>().CreateLogger<AltruistConfig>();
        logger.LogInformation("📦 Loading services...");

        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
             .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
             .ToArray();

        var typesWithServiceAttr = assemblies
            .SelectMany(SafeGetTypes)
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetCustomAttributes<ServiceAttribute>().Any());

        foreach (var type in typesWithServiceAttr)
        {
            foreach (var attr in type.GetCustomAttributes<ServiceAttribute>())
            {
                var serviceType = attr.ServiceType ?? InferServiceType(type);
                if (serviceType == null)
                {
                    logger.LogWarning($"⚠️ Unable to infer service type for {type.FullName}. Skipping registration.");
                    continue;
                }

                services.Add(new ServiceDescriptor(serviceType, type, attr.Lifetime));
                logger.LogInformation($"✅ Registered {type.FullName} as {serviceType.FullName} ({attr.Lifetime})");
            }
        }
    }

    private static Type InferServiceType(Type implementationType)
    {
        if (implementationType.IsGenericTypeDefinition)
        {
            throw new InvalidOperationException(
                $"Cannot infer service type for open generic type '{implementationType.FullName}'. " +
                $"Please specify the service type explicitly in the [Service] attribute.");
        }

        return implementationType;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }
}