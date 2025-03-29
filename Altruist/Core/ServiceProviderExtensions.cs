using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

public static class IServiceProviderExtensions
{
    public static IEnumerable<TType> GetAll<TType>(this IServiceProvider provider)
    {
        var site = typeof(ServiceProvider).GetProperty("CallSiteFactory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider);
        var desc = site!.GetType().GetField("_descriptors", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(site) as ServiceDescriptor[];

        // Skip services that throw exceptions during GetRequiredService
        return desc!
            .Select(s =>
            {
                try
                {
                    return (TType)provider.GetRequiredService(s.ServiceType)!;
                }
                catch
                {
                    return default!;
                }
            })
            .Where(service => service != null);  // Filter out null values
    }
}
