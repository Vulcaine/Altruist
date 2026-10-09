// BodyApi.cs
using System.Numerics;

using Altruist.Physx.Contracts;
using Altruist.TwoD.Numerics;

namespace Altruist.Physx.TwoD
{

    /// <summary>
    /// A 2D rigid body (engine-agnostic). Conventions: world units, +Y up, angles in radians
    /// counter-clockwise positive (Box2D), velocities per second.
    /// <para>Create one with <see cref="IPhysxWorldEngine2D.CreateBody"/> (standalone
    /// worlds; preferred) or, in a world-organizer setup, through <see cref="IPhysxBodyApiProvider2D"/>.
    /// To change its motion use the layers built on it: <see cref="BodyMotionExtensions2D"/> (physics
    /// layer: one velocity formula per call), <see cref="BodySteeringExtensions2D"/> (simple
    /// move/face/impulse helpers) and the Gaming package's <c>GameplayVerbs2D</c> (intent-level verbs).</para>
    /// <para>Threading: not thread-safe; touch a body only from the thread that steps its world, and
    /// not from another thread while it steps.</para>
    /// <example><code>
    /// var body = world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Dynamic, Position = new(0, 2) });
    /// world.CreateFixture(body, new PhysxFixtureDef2D { Shape = PhysxShape2D.Circle(0.5f), Density = 1f });
    /// body.LinearVelocity = new Vector2(3, 0);
    /// </code></example>
    /// </summary>
    public interface IPhysxBody2D : IPhysxBody
    {
        /// <summary>Body origin in world units. On Box2D, setting it calls <c>SetTransform</c> (keeps
        /// the angle, refreshes the broad-phase); prefer <see cref="SetTransform"/> to
        /// change both position and angle.</summary>
        Vector2 Position { get; set; }

        /// <summary>Linear velocity of the center of mass in units/s. On Box2D, setting a non-zero value
        /// wakes the body; static bodies ignore it.</summary>
        Vector2 LinearVelocity { get; set; }

        /// <summary>Angular velocity in rad/s, counter-clockwise positive. On Box2D, setting a non-zero
        /// value wakes the body; static bodies ignore it.</summary>
        float AngularVelocityZ { get; set; }

        /// <summary>Rotation in radians, counter-clockwise positive (local +Y is the body's "up").
        /// Not wrapped: it accumulates turns. Setting it on Box2D calls <c>SetTransform</c>.</summary>
        float RotationZ { get; set; }

        // Rigid-body members. Engine-backed bodies implement them natively; the defaults keep
        // lightweight implementations (fakes, kinematic stand-ins) working.

        /// <summary>Game data carried by the body (a tag, the owning entity, ...).</summary>
        object? UserData { get => null; set { } }

        /// <summary>A sleeping body is skipped by the solver until something wakes it.</summary>
        bool IsAwake { get => true; set { } }

        /// <summary>A disabled body takes no part in the simulation (no contacts, no motion).</summary>
        bool IsEnabled { get => true; set { } }

        /// <summary>Center of mass in world coordinates (the default implementation returns
        /// <see cref="Position"/>).</summary>
        Vector2 WorldCenter => Position;

        /// <summary>Moves and rotates the body in one call (radians, counter-clockwise). Teleports: no
        /// velocity change, no swept collision.</summary>
        /// <param name="position">New body origin (world).</param>
        /// <param name="angle">New rotation in radians.</param>
        void SetTransform(Vector2 position, float angle)
        {
            Position = position;
            RotationZ = angle;
        }

        /// <summary>A body-local direction in world coordinates.</summary>
        Vector2 GetWorldVector(Vector2 local) => Rotation2D.FromRadians(RotationZ).Rotate(local);

        /// <summary>A world direction in body-local coordinates.</summary>
        Vector2 GetLocalVector(Vector2 world) => Rotation2D.FromRadians(RotationZ).Unrotate(world);

        /// <summary>A body-local point in world coordinates.</summary>
        Vector2 GetWorldPoint(Vector2 local) => Position + GetWorldVector(local);

        /// <summary>A world point in body-local coordinates.</summary>
        Vector2 GetLocalPoint(Vector2 world) => GetLocalVector(world - Position);
    }


    /// <summary>
    /// The 2D physics world as used by the game-world organizer (one per world index). A thin
    /// facade over an <see cref="IPhysxWorldEngine2D"/> (<see cref="Altruist.Physx.PhysxWorld2D"/>).
    /// For standalone simulations (rooms, prediction, rollback) use
    /// <see cref="IPhysxWorldEngine2D"/> directly, which also exposes body/fixture creation,
    /// contacts and listeners.
    /// </summary>
    public interface IPhysxWorld2D : IPhysxWorld
    {
        /// <summary>The engine behind this world: create bodies and fixtures in it
        /// (<see cref="IPhysxBodyApiProvider2D.CreateBody"/> does), install a contact listener, query contacts.</summary>
        IPhysxWorldEngine2D Engine { get; }

        /// <summary>Registers a body created by the matching provider with this world.</summary>
        void AddBody(IPhysxBody2D body);

        /// <summary>Removes and destroys a body (its contacts end). No-op for unknown bodies.</summary>
        void RemoveBody(IPhysxBody body);

        /// <summary>The closest <paramref name="maxHits"/> bodies along the ray, closest first
        /// (see <see cref="IPhysxWorldEngine2D.RayCast(PhysxRay2D,int)"/>).</summary>
        IEnumerable<PhysxRaycastHit2D> RayCast(PhysxRay2D ray, int maxHits = 1);
    }


    /// <summary>
    /// Creates bodies in a world and attaches <see cref="Collider2D"/> colliders to them: the world-organizer
    /// path (DI service; the Box2D implementation is registered when <c>altruist:environment:mode</c> is
    /// <c>2D</c>). Stateless: every call names the world or body it works on. For standalone worlds prefer
    /// <see cref="IPhysxWorldEngine2D.CreateBody"/> and <see cref="IPhysxWorldEngine2D.CreateFixture"/>, which
    /// take full definitions (material, filter, sleep, damping).
    /// </summary>
    public interface IPhysxBodyApiProvider2D
    {
        /// <summary>Creates a body in <paramref name="world"/> (already added: no
        /// <see cref="IPhysxWorld2D.AddBody"/> needed). A dynamic body with <paramref name="mass"/> &gt; 0 is
        /// rescaled to exactly that mass whenever a collider is attached; otherwise the mass follows the
        /// colliders' densities.</summary>
        /// <param name="world">The world the body lives in.</param>
        /// <param name="type">Static, dynamic or kinematic.</param>
        /// <param name="mass">Requested mass of a dynamic body (0 or less: derived from the colliders).</param>
        /// <param name="transform">Initial position and rotation.</param>
        IPhysxBody2D CreateBody(IPhysxWorld2D world, PhysxBodyType type, float mass, Transform2D transform);

        /// <summary>Attaches a detached <see cref="Collider2D"/> (from <see cref="PhysxCollider2D"/> or
        /// <see cref="IPhysxColliderApiProvider2D"/>) to a body of a world: creates its fixtures and records it in
        /// the body's colliders.</summary>
        void AddCollider(IPhysxBody2D body, IPhysxCollider2D collider);

        /// <summary>Detaches the collider from its body and destroys its fixtures; no-op when it is not attached.</summary>
        void RemoveCollider(IPhysxCollider2D collider);
    }
}
