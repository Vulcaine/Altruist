using Altruist.Physx;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Altruist.Physx;

[Collection("CollisionHandlerRegistry")]
public class CollisionHandlerRegistryTests
{
    private class RegistryBase { }
    private sealed class RegistryDerived : RegistryBase { }
    private sealed class RegistryOther { }
    private sealed class RegistryEvent { }

    private static CollisionHandlerRegistry.HandlerDescriptor Descriptor(Type a, Type b) =>
        new(typeof(CollisionHandlerRegistryTests), a, b, typeof(RegistryEvent), (Action<object?, object, object>)((_, _, _) => { }));

    [Fact]
    public void returned_list_is_a_snapshot_unaffected_by_later_registrations()
    {
        CollisionHandlerRegistry.Clear();
        CollisionHandlerRegistry.Register(Descriptor(typeof(RegistryOther), typeof(RegistryOther)));

        var before = CollisionHandlerRegistry.GetHandlers(typeof(RegistryOther), typeof(RegistryOther));
        CollisionHandlerRegistry.Register(Descriptor(typeof(RegistryOther), typeof(RegistryOther)));

        before.Should().HaveCount(1);
        before.Should().BeAssignableTo<ICollection<CollisionHandlerRegistry.HandlerDescriptor>>()
            .Which.IsReadOnly.Should().BeTrue();
        CollisionHandlerRegistry.GetHandlers(typeof(RegistryOther), typeof(RegistryOther)).Should().HaveCount(2);
        CollisionHandlerRegistry.Clear();
    }

    [Fact]
    public void symmetric_registration_counts_once()
    {
        CollisionHandlerRegistry.Clear();

        CollisionHandlerRegistry.Register(Descriptor(typeof(RegistryBase), typeof(RegistryOther)));

        CollisionHandlerRegistry.TotalHandlerCount.Should().Be(1);
        CollisionHandlerRegistry.Clear();
    }

    [Fact]
    public void handler_for_a_base_type_matches_derived_runtime_types_in_either_order()
    {
        CollisionHandlerRegistry.Clear();
        var descriptor = Descriptor(typeof(RegistryBase), typeof(RegistryOther));
        CollisionHandlerRegistry.Register(descriptor);

        CollisionHandlerRegistry.GetHandlers(typeof(RegistryDerived), typeof(RegistryOther), typeof(RegistryEvent))
            .Should().ContainSingle().Which.Should().BeSameAs(descriptor);
        CollisionHandlerRegistry.GetHandlers(typeof(RegistryOther), typeof(RegistryDerived), typeof(RegistryEvent))
            .Should().ContainSingle().Which.Should().BeSameAs(descriptor);
        CollisionHandlerRegistry.HasHandlers(typeof(RegistryDerived), typeof(RegistryOther)).Should().BeTrue();
        CollisionHandlerRegistry.Clear();
    }

    [Fact]
    public void non_symmetric_registration_matches_only_the_declared_order()
    {
        CollisionHandlerRegistry.Clear();
        CollisionHandlerRegistry.Register(Descriptor(typeof(RegistryBase), typeof(RegistryOther)), alsoRegisterSymmetric: false);

        CollisionHandlerRegistry.HasHandlers(typeof(RegistryDerived), typeof(RegistryOther)).Should().BeTrue();
        CollisionHandlerRegistry.HasHandlers(typeof(RegistryOther), typeof(RegistryDerived)).Should().BeFalse();
        CollisionHandlerRegistry.Clear();
    }

    [Fact]
    public void assembly_scan_fails_when_the_factory_cannot_resolve_a_handler()
    {
        CollisionHandlerRegistry.Clear();

        var act = () => CollisionHandlerDiscovery.RegisterCollisionHandlers(
            [typeof(CollisionHandlerRegistryTests).Assembly], _ => null, NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>();
        CollisionHandlerRegistry.Clear();
    }

    [Fact]
    public void explicit_type_registration_fails_when_the_factory_cannot_resolve_a_handler()
    {
        CollisionHandlerRegistry.Clear();

        var act = () => CollisionHandlerDiscovery.RegisterCollisionHandlerTypes(
            [typeof(RegistryOther)], _ => null, NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>();
        CollisionHandlerRegistry.Clear();
    }
}
