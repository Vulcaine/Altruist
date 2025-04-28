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
using Microsoft.Extensions.DependencyInjection;

public static class IServiceProviderExtensions
{
    public static IEnumerable<TType> GetAll<TType>(this IServiceProvider provider)
    {
        var properties = provider.GetType().GetProperties();
        var hasServiceProviderProperty = properties.Any(p => p.PropertyType == typeof(IServiceProvider));
        object? site;
        provider.GetType().GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        ServiceDescriptor[] desc = [];
        if (hasServiceProviderProperty)
        {
            var serviceProvider = properties.Single(p => p.PropertyType == typeof(IServiceProvider)).GetValue(provider) as IServiceProvider;
            site = serviceProvider!.GetType().GetProperty("CallSiteFactory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(serviceProvider);
            desc = site!.GetType().GetField("_descriptors", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(site) as ServiceDescriptor[] ?? [];
        }
        else
        {
            site = provider.GetType().GetProperty("CallSiteFactory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider);
            desc = site!.GetType().GetField("_descriptors", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(site) as ServiceDescriptor[] ?? [];
        }

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
