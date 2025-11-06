// Box2DPhysxBodyApiProvider2D.cs
using System.Numerics;
using System.Runtime.InteropServices;
using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;
using Altruist.Physx.TwoD.Numerics;
using Box2DSharp.Dynamics;

namespace Altruist.Physx
{

    class Body2DAdapter : IPhysxBody2D
    {
        public string Id { get; }
        public PhysxBodyType Type { get; set; }
        public float Mass { get => (float)_body.Mass; set { /* Box2D mass from fixtures; ignore set */ } }
        public object? UserData { get => _body.UserData; set => _body.UserData = value; }

        public Vector2 Position
        {
            get => _body.GetPosition();
            set => _body.SetTransform(value, _body.GetAngle());
        }

        public float RotationZ
        {
            get => _body.GetAngle();
            set => _body.SetTransform(_body.GetPosition(), value);
        }

        public Vector2 LinearVelocity
        {
            get => _body.LinearVelocity;
            set => _body.SetLinearVelocity(value);
        }

        public float AngularVelocityZ
        {
            get => _body.AngularVelocity;
            set => _body.SetAngularVelocity(value);
        }

        internal Body Underlying => _body;

        private readonly Body _body;
        private readonly List<IPhysxCollider> _colliders = new();

        public Body2DAdapter(string id, Body body, PhysxBodyType type)
        {
            Id = id;
            _body = body;
            Type = type;
        }

        public void AddCollider(IPhysxCollider collider) => _colliders.Add(collider);

        public bool RemoveCollider(IPhysxCollider collider)
        {
            // If the concrete collider exposes the underlying Fixture, destroy it.
            var fixtureProp = collider.GetType().GetProperty(
                "Fixture",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);

            if (fixtureProp?.GetValue(collider) is Fixture fx)
                _body.DestroyFixture(fx);

            return _colliders.Remove(collider);
        }

        public ReadOnlySpan<IPhysxCollider> GetColliders()
            => CollectionsMarshal.AsSpan(_colliders);

        public void ApplyForce(in PhysxForce force)
        {
            switch (force.Type)
            {
                case PhysxForce.Kind.AddForce2D:
                    _body.ApplyForce(new Vector2(force.Vector.X, force.Vector.Y), _body.GetWorldCenter(), true);
                    break;
                case PhysxForce.Kind.AddImpulse2D:
                    _body.ApplyLinearImpulse(new Vector2(force.Vector.X, force.Vector.Y), _body.GetWorldCenter(), true);
                    break;
                case PhysxForce.Kind.AddTorque2D:
                    _body.ApplyTorque(force.Vector.Z, true);
                    break;
                case PhysxForce.Kind.SetLinearVelocity2D:
                    _body.SetLinearVelocity(new Vector2(force.Vector.X, force.Vector.Y));
                    break;
                case PhysxForce.Kind.SetAngularVelocity2D:
                    _body.SetAngularVelocity(force.Vector.Z);
                    break;
            }
        }

        public bool TryGetColliderById(string colliderId, out IPhysxCollider collider)
        {
            if (string.IsNullOrEmpty(colliderId))
            {
                collider = default!;
                return false;
            }

            foreach (var c in _colliders)
            {
                // If the collider exposes an Id property, use it (like the 3D pattern).
                var idProp = c.GetType().GetProperty(
                    "Id",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);

                if (idProp != null && idProp.GetValue(c) is string id &&
                    string.Equals(id, colliderId, StringComparison.Ordinal))
                {
                    collider = c;
                    return true;
                }
            }

            collider = default!;
            return false;
        }

        public IPhysxCollider? GetColliderAt(int index)
        {
            if ((uint)index < (uint)_colliders.Count)
                return _colliders[index];
            return null;
        }
    }

    /// <summary>
    /// Creates Box2D bodies inside the Box2D engine world and returns a Body2DAdapter.
    /// Caller must then register with the world via IPhysxWorld2D.AddBody(adapter).
    /// </summary>
    public sealed class Box2DPhysxBodyApiProvider2D : IPhysxBodyApiProvider2D
    {
        private readonly Box2DWorldEngine2D _engine;

        public Box2DPhysxBodyApiProvider2D(IPhysxWorldEngine2D engine)
        {
            _engine = engine as Box2DWorldEngine2D
                      ?? throw new InvalidOperationException("Engine must be a Box2D-backed engine.");
        }

        public IPhysxBody2D CreateBody(PhysxBodyType type, float mass, Transform2D transform)
        {
            var bd = new BodyDef
            {
                Position = transform.Position.ToVector2(),
                Angle = transform.Rotation.Radians,
                BodyType = type switch
                {
                    PhysxBodyType.Dynamic => BodyType.DynamicBody,
                    PhysxBodyType.Kinematic => BodyType.KinematicBody,
                    _ => BodyType.StaticBody
                }
            };

            var body = _engine.World.CreateBody(bd);
            var id = Guid.NewGuid().ToString("N");
            var adapter = new Body2DAdapter(id, body, type);

            return adapter;
        }
    }
}
