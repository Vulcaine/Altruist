// Box2DPhysxBodyApiProvider2D.cs
using System.Numerics;
using System.Runtime.InteropServices;

using Altruist.Physx.Contracts;
using Altruist.TwoD.Numerics;

using Box2DSharp.Collision.Shapes;
using Box2DSharp.Dynamics;

namespace Altruist.Physx.TwoD
{

    /// <summary>
    /// <see cref="IPhysxBody2D"/> over a native Box2D body (whose <c>UserData</c> points back here).
    /// Created by <see cref="Box2DWorldEngine2D.CreateBody"/> or
    /// <see cref="Box2DPhysxBodyApiProvider2D.CreateBody"/>. All members read / write the native body
    /// directly (Box2D semantics: setting a non-zero velocity wakes the body; static bodies ignore
    /// velocity; position / angle setters call <c>SetTransform</c>). Not thread-safe.
    /// </summary>
    public class Body2DAdapter : IPhysxBody2D
    {
        /// <summary>Unique body id (from <see cref="PhysxBodyDef2D.Id"/> or a new GUID).</summary>
        public string Id { get; }

        /// <summary>The native Box2D body type. Setting it changes the native body (Box2D resets the
        /// velocity of a body made static and recomputes its mass), so a static body can be made dynamic and
        /// back.</summary>
        public PhysxBodyType Type
        {
            get => TypeOf(_body.BodyType);
            set
            {
                // Changing the type destroys the body's contacts.
                if (Engine is { } e) e.ContactGeneration++;
                _body.BodyType = NativeTypeOf(value);
                if (value == PhysxBodyType.Dynamic) RecomputeDynamicMass();
            }
        }

        // Box2DSharp's BodyType setter keeps the old type's mass data (a static body made dynamic stays at mass 0
        // and never moves). Recompute it from the fixtures like Box2D's SetType (ResetMassData) does, through the
        // public API: fixture masses summed, centroid weighted, inertia about the body origin; SetMassData turns a
        // massless dynamic body into mass 1.
        private void RecomputeDynamicMass()
        {
            var total = new MassData();
            var weightedCenter = Vector2.Zero;
            foreach (var fixture in _body.FixtureList)
            {
                if (fixture.Density == 0f) continue;
                fixture.GetMassData(out var md);
                total.Mass += md.Mass;
                weightedCenter += md.Mass * md.Center;
                total.RotationInertia += md.RotationInertia;
            }
            if (total.Mass > 0f) total.Center = weightedCenter / total.Mass;
            _body.SetMassData(total);
            if (RequestedMass is { } mass) Mass = mass;
        }

        internal static PhysxBodyType TypeOf(BodyType native) => native switch
        {
            BodyType.DynamicBody => PhysxBodyType.Dynamic,
            BodyType.KinematicBody => PhysxBodyType.Kinematic,
            _ => PhysxBodyType.Static,
        };

        internal static BodyType NativeTypeOf(PhysxBodyType type) => type switch
        {
            PhysxBodyType.Dynamic => BodyType.DynamicBody,
            PhysxBodyType.Kinematic => BodyType.KinematicBody,
            _ => BodyType.StaticBody,
        };

        /// <summary>The mass requested through <see cref="Box2DPhysxBodyApiProvider2D.CreateBody"/>, re-applied
        /// after each collider it attaches (null: the mass follows the fixtures).</summary>
        internal float? RequestedMass { get; set; }
        /// <summary>
        /// Box2D derives mass from the fixtures; setting it scales mass and rotational inertia so
        /// the body weighs exactly this much whatever its shape (set it after adding fixtures).
        /// </summary>
        public float Mass
        {
            get => (float)_body.Mass;
            set
            {
                _body.GetMassData(out var md);
                if (md.Mass <= 0) return;
                var k = value / md.Mass;
                md.Mass = value;
                md.RotationInertia *= k;
                _body.SetMassData(md);
            }
        }

        /// <inheritdoc/>
        public object? UserData { get; set; }

        /// <inheritdoc/>
        public bool IsAwake
        {
            get => _body.IsAwake;
            set => _body.IsAwake = value;
        }

        /// <summary>Box2D's enabled flag. Disabling destroys the body's contacts (end-contact fires)
        /// and invalidates contact views; enabling re-creates its broad-phase entries.</summary>
        public bool IsEnabled
        {
            get => _body.IsEnabled;
            set
            {
                // Disabling destroys the body's contacts.
                if (Engine is { } e) e.ContactGeneration++;
                _body.IsEnabled = value;
            }
        }

        /// <summary>Center of mass in world coordinates (Box2D <c>GetWorldCenter</c>).</summary>
        public Vector2 WorldCenter => _body.GetWorldCenter();

        /// <inheritdoc/>
        public void SetTransform(Vector2 position, float angle) => _body.SetTransform(position, angle);

        /// <inheritdoc/>
        public Vector2 GetWorldVector(Vector2 local) => _body.GetWorldVector(local);

        /// <inheritdoc/>
        public Vector2 GetLocalVector(Vector2 world) => _body.GetLocalVector(world);

        /// <inheritdoc/>
        public Vector2 GetWorldPoint(Vector2 local) => _body.GetWorldPoint(local);

        /// <inheritdoc/>
        public Vector2 GetLocalPoint(Vector2 world) => _body.GetLocalPoint(world);

        /// <summary>Optional layer tag (not used by the Box2D engine itself).</summary>
        public PhysxTag? PhysxTag { get; set; }

        /// <inheritdoc/>
        public Vector2 Position
        {
            get => _body.GetPosition();
            set => _body.SetTransform(value, _body.GetAngle());
        }

        /// <inheritdoc/>
        public float RotationZ
        {
            get => _body.GetAngle();
            set => _body.SetTransform(_body.GetPosition(), value);
        }

        /// <inheritdoc/>
        public Vector2 LinearVelocity
        {
            get => _body.LinearVelocity;
            set => _body.SetLinearVelocity(value);
        }

        /// <inheritdoc/>
        public float AngularVelocityZ
        {
            get => _body.AngularVelocity;
            set => _body.SetAngularVelocity(value);
        }

        internal Body Underlying => _body;

        /// <summary>The engine the body was added to (null until then).</summary>
        internal Box2DWorldEngine2D? Engine { get; set; }

        private readonly Body _body;
        private readonly List<IPhysxCollider> _colliders = new();

        /// <summary>Wraps a native body and sets its <c>UserData</c> to this adapter. Normally called by
        /// the engine / provider; the body must still be added to an engine to take part in queries.</summary>
        /// <param name="id">Body id.</param>
        /// <param name="body">The native Box2DSharp body.</param>
        /// <param name="type">The body type; the native body is set to it when they differ.</param>
        public Body2DAdapter(string id, Body body, PhysxBodyType type)
        {
            Id = id;
            _body = body;
            if (body.BodyType != NativeTypeOf(type)) body.BodyType = NativeTypeOf(type);
            body.UserData = this;
        }

        /// <summary>Records <paramref name="collider"/> in this body's collider list only (does not
        /// create a native fixture; use <see cref="IPhysxWorldEngine2D.CreateFixture"/> or
        /// <see cref="Box2DPhysxBodyApiProvider2D.AddCollider"/> for that).</summary>
        public void AddCollider(IPhysxCollider collider) => _colliders.Add(collider);

        /// <summary>Removes <paramref name="collider"/> from this body: an engine fixture of this body
        /// (<see cref="IPhysxWorldEngine2D.CreateFixture"/>) is destroyed; a <see cref="Collider2D"/> attached to
        /// this body has all its fixtures destroyed and becomes detached. End-contact events fire for the
        /// destroyed fixtures' touching contacts. Returns whether it was in the list.</summary>
        public bool RemoveCollider(IPhysxCollider collider)
        {
            if (!_colliders.Remove(collider))
                return false;

            switch (collider)
            {
                case Box2DFixture2D fixture:
                    DestroyNative(fixture);
                    break;
                case Collider2D attached:
                    foreach (var fixture in attached.Fixtures)
                        DestroyNative(fixture);
                    attached.Detach();
                    break;
            }
            return true;
        }

        private void DestroyNative(Box2DFixture2D fixture)
        {
            if (Engine is { } e) e.ContactGeneration++;
            _body.DestroyFixture(fixture.Native);
        }

        /// <summary>The colliders recorded with <see cref="AddCollider"/> (a view over the internal list;
        /// do not hold it across changes).</summary>
        public ReadOnlySpan<IPhysxCollider> GetColliders()
            => CollectionsMarshal.AsSpan(_colliders);

        /// <summary>Applies a 2D force kind at the center of mass (force / impulse / torque wake the body;
        /// set-velocity kinds are plain setters). Non-2D kinds are ignored.</summary>
        /// <param name="force">The force; 2D kinds read <c>Vector.X/Y</c>, torque and angular velocity read <c>Vector.Z</c>.</param>
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

        /// <summary>Finds a recorded collider by its <see cref="IPhysxCollider.Id"/> (ordinal comparison).</summary>
        /// <param name="colliderId">The id; null or empty never matches.</param>
        /// <param name="collider">The match, or <c>default</c>.</param>
        public bool TryGetColliderById(string colliderId, out IPhysxCollider collider)
        {
            if (string.IsNullOrEmpty(colliderId))
            {
                collider = default!;
                return false;
            }

            foreach (var c in _colliders)
            {
                if (string.Equals(c.Id, colliderId, StringComparison.Ordinal))
                {
                    collider = c;
                    return true;
                }
            }

            collider = default!;
            return false;
        }

        /// <summary>The recorded collider at <paramref name="index"/>, or null when out of range.</summary>
        public IPhysxCollider? GetColliderAt(int index)
        {
            if ((uint)index < (uint)_colliders.Count)
                return _colliders[index];
            return null;
        }
    }

    /// <summary>
    /// The Box2D <see cref="IPhysxBodyApiProvider2D"/>: creates bodies in the world it is given and attaches
    /// <see cref="Collider2D"/>s to them as Box2D fixtures (a capsule as a box plus two circles). Stateless and
    /// shared by every world. DI: registered when <c>altruist:environment:mode</c> = <c>2D</c>.
    /// <para>The fixtures of an attached collider get its sensor flag and user data (the
    /// <see cref="ContactRouter2D"/> tag), density 1 on dynamic bodies (else 0) and Box2D's default material
    /// and filter; see <see cref="Collider2D"/> for its events. For a material or filter use
    /// <see cref="IPhysxWorldEngine2D.CreateBody"/> / <see cref="IPhysxWorldEngine2D.CreateFixture"/>.</para>
    /// </summary>
    [Service(typeof(IPhysxBodyApiProvider2D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "2D")]
    public sealed class Box2DPhysxBodyApiProvider2D : IPhysxBodyApiProvider2D
    {
        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">The world's engine is not Box2D.</exception>
        public IPhysxBody2D CreateBody(IPhysxWorld2D world, PhysxBodyType type, float mass, Transform2D transform)
        {
            if (world.Engine is not Box2DWorldEngine2D engine)
                throw new InvalidOperationException("The world's engine must be a Box2DWorldEngine2D.");

            var body = (Body2DAdapter)engine.CreateBody(new PhysxBodyDef2D
            {
                Type = type,
                Position = transform.Position.ToVector2(),
                Angle = transform.Rotation.Radians,
            });
            if (type == PhysxBodyType.Dynamic && mass > 0f)
                body.RequestedMass = mass;
            return body;
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">The body is not a Box2D body of a world, the collider is not a
        /// <see cref="Collider2D"/> (engine fixtures are created with <see cref="IPhysxWorldEngine2D.CreateFixture"/>),
        /// or it is already attached.</exception>
        public void AddCollider(IPhysxBody2D body, IPhysxCollider2D collider)
        {
            if (body is not Body2DAdapter { Engine: { } engine } owner)
                throw new InvalidOperationException("The body must be a Box2D body of a world (create it with CreateBody).");
            if (collider is not Collider2D detached)
                throw new InvalidOperationException("Attach colliders from PhysxCollider2D or IPhysxColliderApiProvider2D; create engine fixtures with IPhysxWorldEngine2D.CreateFixture.");
            if (detached.IsAttached)
                throw new InvalidOperationException("This collider is already attached.");

            var density = owner.Type == PhysxBodyType.Dynamic ? 1f : 0f;
            var fixtures = ShapesOf(detached)
                .Select(shape => engine.CreateNativeFixture(owner, new PhysxFixtureDef2D
                {
                    Shape = shape,
                    Density = density,
                    IsTrigger = detached.IsTrigger,
                    UserData = detached.UserData,
                }))
                .ToArray();
            detached.Attach(fixtures);
            owner.AddCollider(detached);

            if (owner.RequestedMass is { } mass && owner.Type == PhysxBodyType.Dynamic)
                owner.Mass = mass;
        }

        /// <inheritdoc/>
        public void RemoveCollider(IPhysxCollider2D collider)
        {
            if (collider is Collider2D { AttachedBody: Body2DAdapter body })
                body.RemoveCollider(collider);
        }

        // The collider's Box2D shapes in body space (its transform is the offset and rotation in the body).
        private static IEnumerable<PhysxShape2D> ShapesOf(Collider2D c)
        {
            var t = c.Transform;
            var center = t.Position.ToVector2();
            var angle = t.Rotation.Radians;
            switch (c.Shape)
            {
                case PhysxColliderShape2D.Circle2D:
                    yield return PhysxShape2D.Circle(t.Size.X, center);
                    break;
                case PhysxColliderShape2D.Box2D:
                    yield return PhysxShape2D.Box(t.Size.X, t.Size.Y, center, angle);
                    break;
                case PhysxColliderShape2D.Capsule2D:
                    {
                        var radius = t.Size.X;
                        var halfLength = t.Size.Y;
                        var axis = t.Rotation.Rotate(new Vector2(halfLength, 0f));
                        yield return PhysxShape2D.Box(halfLength, radius, center, angle);
                        yield return PhysxShape2D.Circle(radius, center - axis);
                        yield return PhysxShape2D.Circle(radius, center + axis);
                        break;
                    }
                case PhysxColliderShape2D.Polygon2D:
                    yield return PhysxShape2D.Polygon(c.Vertices!.Select(v => t.Rotation.Rotate(v) + center));
                    break;
                default:
                    throw new NotSupportedException($"Unsupported collider shape: {c.Shape}");
            }
        }
    }
}
