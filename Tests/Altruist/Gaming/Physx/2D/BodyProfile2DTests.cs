/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming.TwoD;
using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;
using FluentAssertions;
using Xunit;

namespace Altruist.Gaming.Tests.TwoD;

public class BodyProfile2DTests
{
    [Fact]
    public void HumanoidCapsuleBodyProfile2D_CreateBody_ReturnsDynamicType()
    {
        var profile = new HumanoidCapsuleBodyProfile2D(0.3f, 0.9f, 75f);
        var transform = new Transform2D(Position2D.Of(0, 0), Size2D.Of(1, 2), Scale2D.One, Rotation2D.Zero);

        var (type, mass) = profile.CreateBody(transform);

        type.Should().Be(PhysxBodyType.Dynamic);
        mass.Should().Be(75f);
    }

    [Fact]
    public void HumanoidCapsuleBodyProfile2D_CreateColliders_ReturnsCapsule()
    {
        var profile = new HumanoidCapsuleBodyProfile2D(0.3f, 0.9f, 75f);
        var transform = new Transform2D(Position2D.Of(0, 0), Size2D.One, Scale2D.One, Rotation2D.Zero);

        var colliders = profile.CreateColliders(transform).ToList();

        colliders.Should().HaveCount(1);
        colliders[0].Shape.Should().Be(PhysxColliderShape2D.Capsule2D);
    }

    [Fact]
    public void HumanoidCapsuleBodyProfile2D_CreateColliders_ReturnsExactlyOne()
    {
        var profile = new HumanoidCapsuleBodyProfile2D(0.5f, 1.0f, 80f);
        var transform = Transform2D.Zero;

        var colliders = profile.CreateColliders(transform).ToList();

        colliders.Should().HaveCount(1);
    }

    [Fact]
    public void HumanoidCapsuleBodyProfile2D_KinematicFlag_IsPreserved()
    {
        var kinematic = new HumanoidCapsuleBodyProfile2D(0.3f, 0.9f, 75f, isKinematic: true);
        var notKinematic = new HumanoidCapsuleBodyProfile2D(0.3f, 0.9f, 75f, isKinematic: false);

        kinematic.IsKinematic.Should().BeTrue();
        notKinematic.IsKinematic.Should().BeFalse();
    }
}
