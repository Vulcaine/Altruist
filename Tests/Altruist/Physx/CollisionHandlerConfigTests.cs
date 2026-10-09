using Altruist.Physx;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Altruist.Physx;

public class CollisionHandlerConfigTests
{
    [Fact]
    public async Task configures_without_a_logger_factory_in_the_collection_being_configured()
    {
        var services = new ServiceCollection();
        var config = new AltruistCollisionHandlerConfig(NullLoggerFactory.Instance);

        await config.Configure(services);

        config.IsConfigured.Should().BeTrue();
    }
}
