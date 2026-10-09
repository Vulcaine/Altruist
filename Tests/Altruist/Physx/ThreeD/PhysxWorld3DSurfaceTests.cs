using System.Reflection;
using Altruist.Physx.ThreeD;
using FluentAssertions;

namespace Tests.Altruist.Physx.ThreeD;

public class PhysxWorld3DSurfaceTests
{
    [Fact]
    public void physx_world_declares_no_legacy_nested_contracts()
    {
        typeof(PhysxWorld3D).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Should().BeEmpty();
    }

    [Fact]
    public void bepu_engine_declares_no_unused_closest_hit_collector()
    {
        typeof(BepuWorldEngine3D)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Select(t => t.Name)
            .Should().NotContain("ClosestHitCollector");
    }
}
