/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using Altruist.Physx.Contracts;
using Altruist.TwoD.Numerics;

namespace Altruist.Physx.TwoD;

/// <summary>
/// Box2D-backed collider provider. Creates data-only collider adapters
/// from PhysxCollider2DParams descriptors. The actual Box2D Fixture creation
/// happens in Box2DPhysxBodyApiProvider2D.AddCollider() when attaching to a body.
/// </summary>
[Service(typeof(IPhysxColliderApiProvider2D))]
[ConditionalOnConfig("altruist:environment:mode", havingValue: "2D")]
public sealed class Box2DPhysxColliderApiProvider2D : IPhysxColliderApiProvider2D
{
    public IPhysxCollider2D CreateCollider(in PhysxCollider2DParams p)
    {
        return new Collider2DAdapter(
            id: Guid.NewGuid().ToString("N"),
            shape: p.Shape,
            transform: p.Transform,
            isTrigger: p.IsTrigger);
    }

    /// <summary>
    /// Data-only adapter implementing IPhysxCollider2D.
    /// Engine-specific shape creation happens in the body API when attaching.
    /// </summary>
    private sealed class Collider2DAdapter : IPhysxCollider2D
    {
        public string Id { get; }
        public bool IsTrigger { get; set; }
        public object? UserData { get; set; }

        public Transform2D Transform { get; set; }
        public PhysxColliderShape2D Shape { get; }
        public Vector2[]? Vertices { get; }

        public event Action<IPhysxCollider, IPhysxCollider>? OnTriggerEnter;
        public event Action<IPhysxCollider, IPhysxCollider>? OnTriggerExit;
        public event Action<PhysxCollisionInfo2D>? OnCollisionEnter;
        public event Action<PhysxCollisionInfo2D>? OnCollisionStay;
        public event Action<PhysxCollisionInfo2D>? OnCollisionExit;

        public Collider2DAdapter(string id, PhysxColliderShape2D shape, Transform2D transform, bool isTrigger)
        {
            Id = id;
            Shape = shape;
            Transform = transform;
            IsTrigger = isTrigger;
        }

        public void RaiseCollisionEnter(in PhysxCollisionInfo2D info) => OnCollisionEnter?.Invoke(info);
        public void RaiseCollisionStay(in PhysxCollisionInfo2D info) => OnCollisionStay?.Invoke(info);
        public void RaiseCollisionExit(in PhysxCollisionInfo2D info) => OnCollisionExit?.Invoke(info);
        public void RaiseTriggerEnter(IPhysxCollider self, IPhysxCollider other) => OnTriggerEnter?.Invoke(self, other);
        public void RaiseTriggerExit(IPhysxCollider self, IPhysxCollider other) => OnTriggerExit?.Invoke(self, other);
    }
}
