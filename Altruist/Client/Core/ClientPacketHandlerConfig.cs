using System.Reflection;
using Altruist.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Client;

/// <summary>
/// Boot-time discovery: scans every loaded assembly for
/// <see cref="PacketHandlerAttribute"/>-marked classes, registers them as
/// singletons in DI, then resolves each instance and feeds it to
/// <see cref="ClientPacketDispatcher.Register"/>. Mirrors the server's
/// <c>CombatEventHandlerConfig</c>.
///
/// <para>Conditional on <c>altruist:client:transport</c> — without a client
/// transport configured, there are no inbound frames to dispatch, so we skip
/// discovery entirely.</para>
/// </summary>
[ServiceConfiguration]
[ConditionalOnConfig("altruist:client:transport")]
public sealed class ClientPacketHandlerConfig : IAltruistConfiguration
{
    public bool IsConfigured { get; set; }

    public Task Configure(IServiceCollection services)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        var logger = services.BuildServiceProvider()
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<ClientPacketHandlerConfig>();

        var assemblies = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .ToArray();

        var handlerTypes = TypeDiscovery
            .FindTypesWithAttribute<PacketHandlerAttribute>(assemblies)
            .ToArray();

        if (handlerTypes.Length == 0)
        {
            logger.LogDebug("No [PacketHandler] classes discovered. Skipping client packet handler wiring.");
            IsConfigured = true;
            return Task.CompletedTask;
        }

        foreach (var handlerType in handlerTypes)
        {
            services.AddSingleton(handlerType);
        }

        using (var warmupProvider = services.BuildServiceProvider())
        {
            var dispatcher = warmupProvider.GetService<ClientPacketDispatcher>();
            if (dispatcher is null)
            {
                logger.LogWarning(
                    "ClientPacketDispatcher could not be resolved. [PacketHandler] discovery was skipped. " +
                    "Did you forget to reference Altruist.Client?");
                IsConfigured = true;
                return Task.CompletedTask;
            }

            foreach (var handlerType in handlerTypes)
            {
                object? instance;
                try
                {
                    instance = warmupProvider.GetService(handlerType);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex,
                        "Skipping {Type} during [PacketHandler] auto-discovery — not DI-constructible.",
                        handlerType.FullName);
                    continue;
                }

                if (instance is null)
                {
                    logger.LogWarning(
                        "Could not create instance of packet handler {Type}. Skipping.",
                        handlerType.FullName);
                    continue;
                }

                try
                {
                    dispatcher.Register(instance);
                    logger.LogDebug("Registered [PacketHandler] {Type}.", handlerType.FullName);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to register [PacketHandler] {Type}: {Message}",
                        handlerType.FullName, ex.Message);
                }
            }
        }

        logger.LogDebug("Client packet handler discovery complete. {Count} handler types wired.", handlerTypes.Length);
        IsConfigured = true;
        return Task.CompletedTask;
    }
}
