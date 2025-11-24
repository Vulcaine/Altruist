/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.Contracts;
using Altruist.ThreeD.Numerics;

namespace Altruist.Physx.ThreeD
{
    /// <summary>
    /// BEPU-backed collider API provider (3D).
    ///
    /// NOTE: This remains engine-agnostic:
    /// - It does NOT touch the BEPU Simulation directly.
    /// - It only creates an adapter implementing IPhysxCollider3D
    ///   from an engine-agnostic PhysxCollider3DDesc.
    /// The BEPU body API later interprets Shape + Transform + IsTrigger
    /// when attaching this collider to a specific body/engine.
    /// </summary>
    [Service(typeof(IPhysxColliderApiProvider3D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    public sealed class BepuPhysxColliderApiProvider3D : IPhysxColliderApiProvider3D
    {
        public BepuPhysxColliderApiProvider3D()
        {
        }

        /// <summary>
        /// Create an engine-agnostic collider adapter from a collider descriptor.
        /// </summary>
        public IPhysxCollider3D CreateCollider(in PhysxCollider3DDesc desc)
        {
            return new Collider3DAdapter(
                id: desc.Id,
                shape: desc.Shape,
                transform: desc.Transform,
                isTrigger: desc.IsTrigger);
        }

        /// <summary>
        /// Simple data-only adapter implementing IPhysxCollider3D
        /// for BEPU-backed worlds. All engine-specific shape creation
        /// happens in the body API when attaching this collider.
        /// </summary>
        private sealed class Collider3DAdapter : IPhysxCollider3D
        {
            // -----------------------
            // IPhysxCollider (base)
            // -----------------------

            public string Id { get; }

            public bool IsTrigger { get; set; }

            public object? UserData { get; set; }

            // NOTE: signatures must match IPhysxCollider exactly.
            public event Action<IPhysxCollider, IPhysxCollider>? OnTriggerEnter;
            public event Action<IPhysxCollider, IPhysxCollider>? OnTriggerExit;

            // -----------------------
            // IPhysxCollider3D
            // -----------------------

            public Transform3D Transform { get; set; }

            public PhysxColliderShape3D Shape { get; }

            public event Action<PhysxCollisionInfo3D>? OnCollisionEnter;
            public event Action<PhysxCollisionInfo3D>? OnCollisionStay;
            public event Action<PhysxCollisionInfo3D>? OnCollisionExit;

            public Collider3DAdapter(
                string id,
                PhysxColliderShape3D shape,
                Transform3D transform,
                bool isTrigger)
            {
                Id = id ?? throw new ArgumentNullException(nameof(id));
                Shape = shape;
                Transform = transform;
                IsTrigger = isTrigger;
            }

            public void RaiseCollisionEnter(in PhysxCollisionInfo3D info)
                => OnCollisionEnter?.Invoke(info);

            public void RaiseCollisionStay(in PhysxCollisionInfo3D info)
                => OnCollisionStay?.Invoke(info);

            public void RaiseCollisionExit(in PhysxCollisionInfo3D info)
                => OnCollisionExit?.Invoke(info);

            public void RaiseTriggerEnter(IPhysxCollider self, IPhysxCollider other)
                => OnTriggerEnter?.Invoke(self, other);

            public void RaiseTriggerExit(IPhysxCollider self, IPhysxCollider other)
                => OnTriggerExit?.Invoke(self, other);
        }
    }
}
