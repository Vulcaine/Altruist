
using System.Text.Json;

using Altruist;

/// <summary>
/// Supplies a default <see cref="JsonSerializerOptions"/> (case-insensitive, camelCase) to services that inject one
/// when nothing else registers it. Picked up automatically as an <see cref="IServiceFactory"/>; register your own
/// <see cref="JsonSerializerOptions"/> (e.g. via a <see cref="BeanAttribute"/> method) to override it.
/// </summary>
public class JsonOptionsServiceFactory : IServiceFactory
{
    /// <inheritdoc/>
    public bool CanCreate(Type serviceType) => serviceType == typeof(JsonSerializerOptions);
    /// <summary>Creates a new options instance with <see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/> and camelCase naming.</summary>
    /// <param name="serviceProvider">Unused.</param>
    /// <param name="serviceType">Always <see cref="JsonSerializerOptions"/>.</param>
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
