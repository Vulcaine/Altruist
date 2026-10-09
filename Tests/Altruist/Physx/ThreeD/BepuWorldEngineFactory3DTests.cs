using System.Numerics;
using Altruist.Physx.ThreeD;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Altruist.Physx.ThreeD;

public class BepuWorldEngineFactory3DTests
{
    private static readonly Vector3 Gravity = new(0f, -9.81f, 0f);

    [Fact]
    public void each_call_creates_an_independent_engine()
    {
        var factory = new BepuWorldEngineFactory3D();

        using var first = factory.Create(Gravity, 1f / 60f);
        using var second = factory.Create(Gravity, 1f / 60f);

        second.Should().NotBeSameAs(first);
    }

    [Fact]
    public void disposing_one_world_does_not_hand_a_disposed_engine_to_the_next()
    {
        var factory = new BepuWorldEngineFactory3D();
        new PhysxWorld3D(factory.Create(Gravity)).Dispose();

        using var next = factory.Create(Gravity);

        var act = () => next.Step(1f / 60f);
        act.Should().NotThrow();
    }

    [Fact]
    public void resolves_from_di_without_any_heightmap_loader_registration()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IPhysxWorldEngineFactory3D, BepuWorldEngineFactory3D>()
            .BuildServiceProvider();

        var act = () => provider.GetRequiredService<IPhysxWorldEngineFactory3D>();

        act.Should().NotThrow();
    }
}
