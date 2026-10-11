/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Codec.MessagePack;

using MessagePack;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Tests.Altruist.Framework.Networking;

/// <summary>
/// Interceptors registered in DI are picked up by the connection manager (before, only
/// AddInterceptor worked, so an app had to find the live manager and install them by hand), and
/// the context carries the route of the connection so an interceptor can target one portal.
/// </summary>
public sealed class DiInterceptorRegistrationTests
{
    [MessagePackObject]
    public sealed class PingPacket : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 78;
    }

    private sealed class RoutedRejector : IInterceptor
    {
        public readonly List<InterceptContext> Seen = new();

        public Task Intercept(InterceptContext context, IPacket eventData)
        {
            lock (Seen)
                Seen.Add(context);
            if (context.Route == "/game")
                context.Reject();
            return Task.CompletedTask;
        }
    }

    private static ICodecResolver Codecs()
    {
        var codecs = new Mock<ICodecResolver>();
        codecs.Setup(c => c.Resolve(It.IsAny<string?>())).Returns(new MessagePackCodec());
        return codecs.Object;
    }

    private static string RegisterGate(Action handled)
    {
        var name = "test-ping-" + Guid.NewGuid().ToString("N");
        PortalGateRegistry<IPortal>.Register(name, new Func<PingPacket, string, Task>((_, _) => { handled(); return Task.CompletedTask; }));
        return name;
    }

    private static AltruistPacket Packet(string evt) => new() { Event = evt, MessageCode = PacketCodes.Altruist };

    private static byte[] Payload() => MessagePackSerializer.Serialize(new PingPacket());

    [Fact]
    public async Task Interceptors_from_the_container_run_and_see_the_route()
    {
        var rejector = new RoutedRejector();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(new Mock<ISocketManager>().Object);
        services.AddSingleton(Codecs());
        services.AddSingleton<IInterceptor>(rejector);
        services.AddSingleton<ConnectionManager>(sp => new ConnectionManager(
            sp.GetRequiredService<ISocketManager>(), sp.GetRequiredService<ICodecResolver>(),
            sp.GetRequiredService<ILoggerFactory>(), interceptors: sp.GetServices<IInterceptor>()));
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<ConnectionManager>();

        var handled = 0;
        var evt = RegisterGate(() => Interlocked.Increment(ref handled));

        await manager.ProcessPacket(Packet(evt), Payload(), "/game", "c1");
        Assert.Equal(0, handled);
        await manager.ProcessPacket(Packet(evt), Payload(), "/lobby", "c2");
        Assert.Equal(1, handled);
        await manager.ProcessPacket(Packet("no-such-gate-" + Guid.NewGuid().ToString("N")), new byte[] { 1 }, "/game", "c3");

        Assert.Equal(new[] { "/game", "/lobby", "/game" }, rejector.Seen.Select(c => c.Route));
    }

    [Fact]
    public async Task An_interceptor_registered_in_di_and_added_by_hand_runs_once()
    {
        var rejector = new RoutedRejector();
        var manager = new ConnectionManager(new Mock<ISocketManager>().Object, Codecs(), NullLoggerFactory.Instance,
            interceptors: new IInterceptor[] { rejector });
        manager.AddInterceptor(rejector);

        await manager.ProcessPacket(Packet(RegisterGate(() => { })), Payload(), "/lobby", "c1");

        Assert.Single(rejector.Seen);
    }

    [Fact]
    public void Route_defaults_to_empty_for_the_old_constructor()
    {
        Assert.Equal("", new InterceptContext("evt", "c", 3).Route);
        Assert.Equal("/game", new InterceptContext("evt", "c", 3, "/game").Route);
    }
}
