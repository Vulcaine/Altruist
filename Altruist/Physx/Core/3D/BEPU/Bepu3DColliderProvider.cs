/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.Contracts;
using Altruist.ThreeD.Numerics;

namespace Altruist.Physx.ThreeD
{
    /// <summary>
    /// BEPU-backed collider API provider (3D). Registered in DI as a singleton <see cref="IPhysxColliderApiProvider3D"/> when the
    /// config key <c>altruist:environment:mode</c> equals <c>3D</c>.
    /// </summary>
    /// <remarks>
    /// It does not touch the BEPU simulation; it wraps a <see cref="PhysxCollider3DDesc"/> in an
    /// <see cref="IPhysxCollider3D"/> that <see cref="BepuWorldEngine3D"/> bodies accept. The engine turns
    /// <c>Shape</c> + <c>Transform.Size</c> (+ heightfield) into a BEPU shape when the collider is attached, reads
    /// <c>IsTrigger</c> during every timestep, and raises the collider's collision and trigger events.
    /// </remarks>
    [Service(typeof(IPhysxColliderApiProvider3D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    public sealed class BepuPhysxColliderApiProvider3D : IPhysxColliderApiProvider3D
    {
        /// <summary>Creates the stateless provider.</summary>
        public BepuPhysxColliderApiProvider3D()
        {
        }

        /// <summary>Creates a collider from a descriptor; attach it with <see cref="IPhysxBodyApiProvider3D.AddCollider"/>.</summary>
        /// <param name="desc">Collider descriptor (see <see cref="PhysxCollider3D"/>).</param>
        public IPhysxCollider3D CreateCollider(in PhysxCollider3DDesc desc)
            => new BepuCollider3D(desc.Id, desc.Shape, desc.Transform, desc.Heightfield, desc.IsTrigger);
    }

    /// <summary>
    /// Collider created by <see cref="BepuPhysxColliderApiProvider3D"/>. <see cref="BepuWorldEngine3D"/> raises its events;
    /// the shape data is read when it is attached to a body.
    /// </summary>
    internal sealed class BepuCollider3D : IPhysxCollider3D
    {
        public string Id { get; }

        public bool IsTrigger { get; set; }

        public object? UserData { get; set; }

        public event Action<IPhysxCollider, IPhysxCollider>? OnTriggerEnter;
        public event Action<IPhysxCollider, IPhysxCollider>? OnTriggerExit;

        public HeightfieldData? Heightfield { get; set; }

        public Transform3D Transform { get; set; }

        public PhysxColliderShape3D Shape { get; }

        public event Action<PhysxCollisionInfo3D>? OnCollisionEnter;
        public event Action<PhysxCollisionInfo3D>? OnCollisionStay;
        public event Action<PhysxCollisionInfo3D>? OnCollisionExit;

        public BepuCollider3D(
            string id,
            PhysxColliderShape3D shape,
            Transform3D transform,
            HeightfieldData? heightfield,
            bool isTrigger)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Shape = shape;
            Transform = transform;
            IsTrigger = isTrigger;
            Heightfield = heightfield;
        }

        public void RaiseCollisionEnter(in PhysxCollisionInfo3D info) => OnCollisionEnter?.Invoke(info);

        public void RaiseCollisionStay(in PhysxCollisionInfo3D info) => OnCollisionStay?.Invoke(info);

        public void RaiseCollisionExit(in PhysxCollisionInfo3D info) => OnCollisionExit?.Invoke(info);

        public void RaiseTriggerEnter(IPhysxCollider self, IPhysxCollider other) => OnTriggerEnter?.Invoke(self, other);

        public void RaiseTriggerExit(IPhysxCollider self, IPhysxCollider other) => OnTriggerExit?.Invoke(self, other);
    }
}
