
using System.Text.Json;

using Altruist;

using Microsoft.Extensions.DependencyInjection;

[Service(lifetime: ServiceLifetime.Singleton)]
public class JsonOptionsServiceFactory : IServiceFactory
{
    public bool CanCreate(Type serviceType) => serviceType == typeof(JsonSerializerOptions);
    public object Create(IServiceProvider serviceProvider, Type serviceType)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        return options;
    }
}
