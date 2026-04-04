/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;

namespace Altruist.Gaming.TwoD
{
    public interface IBodyProfile2D
    {
        (PhysxBodyType type, float mass) CreateBody(Transform2D transform);
        IEnumerable<PhysxCollider2DParams> CreateColliders(Transform2D transform);
    }

    public sealed class HumanoidCapsuleBodyProfile2D : IBodyProfile2D
    {
        public float Radius { get; }
        public float HalfLength { get; }
        public float Mass { get; }
        public bool IsKinematic { get; }

        public HumanoidCapsuleBodyProfile2D(
            float radius,
            float halfLength,
            float mass,
            bool isKinematic = false)
        {
            Radius = radius;
            HalfLength = halfLength;
            Mass = mass;
            IsKinematic = isKinematic;
        }

        public (PhysxBodyType type, float mass) CreateBody(Transform2D transform)
        {
            return (PhysxBodyType.Dynamic, Mass);
        }

        public IEnumerable<PhysxCollider2DParams> CreateColliders(Transform2D transform)
        {
            var sized = transform.WithSize(Size2D.Of(Radius, HalfLength));
            yield return new PhysxCollider2DParams(PhysxColliderShape2D.Capsule2D, sized, isTrigger: false);
        }
    }
}
