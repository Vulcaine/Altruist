using System.Numerics;

namespace Altruist.Physx.Contracts
{
    /// <summary>
    /// Simulation role of a physics body: <c>Static</c> never moves (infinite mass, ignores forces and gravity);
    /// <c>Dynamic</c> is fully simulated (gravity, forces, impulses, contacts); <c>Kinematic</c> is moved by code through
    /// its velocity/pose, pushes dynamic bodies without being pushed back, and ignores gravity.
    /// </summary>
    public enum PhysxBodyType
    {
        /// <summary>Never moves: infinite mass, ignores forces and gravity (walls, floors, level geometry).</summary>
        Static,
        /// <summary>Fully simulated: affected by gravity, forces, impulses and contacts.</summary>
        Dynamic,
        /// <summary>Moved only by code through its velocity or pose; pushes dynamic bodies, is not pushed back, ignores gravity.</summary>
        Kinematic
    }


    /// <summary>
    /// Engine-agnostic force/velocity command executed by <see cref="IPhysxBody.ApplyForce"/>. Create it with the static
    /// factories (<see cref="Impulse3D"/>, <see cref="LinearVelocity2D"/>, ...).
    /// </summary>
    /// <remarks>
    /// A body only honours the kinds of its own dimension (2D bodies ignore 3D kinds and vice versa); static bodies ignore
    /// all. Forces and torques are continuous: Box2D accumulates <c>AddForce2D</c>/<c>AddTorque2D</c> over the next step, and
    /// BEPU applies <c>AddForce3D</c>/<c>AddTorque3D</c> during every fixed timestep of the next engine step that runs one.
    /// Commands wake the body. For intent-level helpers prefer
    /// <c>BodyMotionExtensions2D</c> / <c>BodySteeringExtensions3D</c>.
    /// </remarks>
    /// <example><code>body.ApplyForce(PhysxForce.Impulse3D(new Vector3(0, 4f, 0)));</code></example>
    public readonly struct PhysxForce
    {
        /// <summary>Command kind; 2D kinds store their data in X/Y (and Z for scalar rotation), 3D kinds in X/Y/Z.</summary>
        public enum Kind
        {
            /// <summary>Continuous 2D force at the centre of mass.</summary>
            AddForce2D,
            /// <summary>Continuous 3D force (BEPU: acts over every timestep of the next engine step).</summary>
            AddForce3D,
            /// <summary>Instantaneous 2D linear impulse at the centre of mass.</summary>
            AddImpulse2D,
            /// <summary>Instantaneous 3D linear impulse.</summary>
            AddImpulse3D,
            /// <summary>2D torque about Z (counter-clockwise positive).</summary>
            AddTorque2D,
            /// <summary>Continuous 3D torque (BEPU: acts over every timestep of the next engine step).</summary>
            AddTorque3D,
            /// <summary>Overwrite the 2D linear velocity.</summary>
            SetLinearVelocity2D,
            /// <summary>Overwrite the 3D linear velocity.</summary>
            SetLinearVelocity3D,
            /// <summary>Overwrite the 2D angular velocity (radians/s, stored in Z).</summary>
            SetAngularVelocity2D,
            /// <summary>Overwrite the 3D angular velocity (radians/s).</summary>
            SetAngularVelocity3D
        }

        /// <summary>Command kind.</summary>
        public Kind Type { get; }
        /// <summary>Payload: a 2D vector is stored as <c>(x, y, 0)</c>, a 2D scalar (torque, angular velocity) as <c>(0, 0, value)</c>.</summary>
        public Vector3 Vector { get; }

        private PhysxForce(Kind type, Vector3 vec)
        {
            Type = type;
            Vector = vec;
        }

        /// <summary>Continuous 2D force at the centre of mass.</summary>
        /// <param name="f">Force (mass·units/s²).</param>
        public static PhysxForce Force2D(Vector2 f) => new(Kind.AddForce2D, new Vector3(f, 0));
        /// <summary>Instantaneous 2D linear impulse at the centre of mass.</summary>
        /// <param name="j">Impulse (mass·units/s).</param>
        public static PhysxForce Impulse2D(Vector2 j) => new(Kind.AddImpulse2D, new Vector3(j, 0));
        /// <summary>2D torque about Z (counter-clockwise positive).</summary>
        /// <param name="tauZ">Torque.</param>
        public static PhysxForce Torque2D(float tauZ) => new(Kind.AddTorque2D, new Vector3(0, 0, tauZ));
        /// <summary>Overwrite the 2D linear velocity.</summary>
        /// <param name="v">New velocity (units/s).</param>
        public static PhysxForce LinearVelocity2D(Vector2 v) => new(Kind.SetLinearVelocity2D, new Vector3(v, 0));
        /// <summary>Overwrite the 2D angular velocity.</summary>
        /// <param name="wZ">New angular velocity (radians/s, counter-clockwise positive).</param>
        public static PhysxForce AngularVelocity2D(float wZ) => new(Kind.SetAngularVelocity2D, new Vector3(0, 0, wZ));

        /// <summary>Continuous 3D force (BEPU applies it during every timestep of the next engine step that runs one).</summary>
        /// <param name="f">Force (mass·units/s²).</param>
        public static PhysxForce Force3D(Vector3 f) => new(Kind.AddForce3D, f);
        /// <summary>Instantaneous 3D linear impulse.</summary>
        /// <param name="j">Impulse (mass·units/s).</param>
        public static PhysxForce Impulse3D(Vector3 j) => new(Kind.AddImpulse3D, j);
        /// <summary>Continuous 3D torque (BEPU applies it during every timestep of the next engine step that runs one).</summary>
        /// <param name="tau">World-space axis × magnitude.</param>
        public static PhysxForce Torque3D(Vector3 tau) => new(Kind.AddTorque3D, tau);
        /// <summary>Overwrite the 3D linear velocity.</summary>
        /// <param name="v">New velocity (units/s).</param>
        public static PhysxForce LinearVelocity3D(Vector3 v) => new(Kind.SetLinearVelocity3D, v);
        /// <summary>Overwrite the 3D angular velocity.</summary>
        /// <param name="w">New angular velocity (radians/s, world-space axis × rate).</param>
        public static PhysxForce AngularVelocity3D(Vector3 w) => new(Kind.SetAngularVelocity3D, w);
    }

    /// <summary>
    /// Predefined query-layer bits for <see cref="PhysxTag.Layer"/>. Bits above 4 are free for application layers.
    /// Used as a filter by 3D ray/capsule casts (a body matches when <c>(tag.Layer &amp; mask) != 0</c>); it does not
    /// filter contacts between bodies.
    /// </summary>
    [Flags]
    public enum PhysxLayer : uint
    {
        /// <summary>No layer; a body tagged with it never matches any query mask.</summary>
        None = 0,
        /// <summary>Terrain / heightfields.</summary>
        Terrain = 1u << 0,
        /// <summary>Static level geometry.</summary>
        StaticWorld = 1u << 1,
        /// <summary>Simulated dynamic objects.</summary>
        Dynamic = 1u << 2,
        /// <summary>Character controllers.</summary>
        Character = 1u << 3,
        /// <summary>Trigger volumes.</summary>
        Trigger = 1u << 4,

        /// <summary><see cref="Terrain"/> | <see cref="StaticWorld"/>: everything that is "the level".</summary>
        World = Terrain | StaticWorld,
        /// <summary>Every bit set; the implicit layer of untagged bodies and the default query mask.</summary>
        All = 0xFFFFFFFFu
    }

    /// <summary>Layer tag attached to a body (<see cref="IPhysxBody.PhysxTag"/>) for query filtering.</summary>
    /// <example><code>var tag = new PhysxTag((uint)(PhysxLayer.Character | PhysxLayer.Dynamic));</code></example>
    public readonly struct PhysxTag
    {
        /// <summary>Layer bit mask (see <see cref="PhysxLayer"/>).</summary>
        public uint Layer { get; }
        /// <summary>Creates a tag.</summary>
        /// <param name="layer">Layer bit mask, usually a cast <see cref="PhysxLayer"/> combination.</param>
        public PhysxTag(uint layer) => Layer = layer;
    }

    /// <summary>
    /// Engine-agnostic physics body shared by the 2D and 3D layers. Dimension-specific interfaces
    /// (<c>IPhysxBody2D</c>, <c>IPhysxBody3D</c>) add pose and velocity.
    /// </summary>
    public interface IPhysxBody
    {
        /// <summary>Unique body identifier (the descriptor id).</summary>
        string Id { get; }
        /// <summary>Simulation role. Whether changing it affects the running simulation depends on the backend.</summary>
        PhysxBodyType Type { get; set; }
        /// <summary>Body mass (0 for static/kinematic in most backends).</summary>
        float Mass { get; set; }

        /// <summary>Attaches a collider. Backends differ in what this does natively (see the backend's body adapter).</summary>
        /// <param name="collider">Collider to attach.</param>
        void AddCollider(IPhysxCollider collider);
        /// <summary>Detaches a collider.</summary>
        /// <param name="collider">Collider to detach.</param>
        /// <returns><see langword="true"/> if it was attached.</returns>
        bool RemoveCollider(IPhysxCollider collider);
        /// <summary>Attached colliders as a span over the internal list; do not hold it across add/remove calls.</summary>
        ReadOnlySpan<IPhysxCollider> GetColliders();
        /// <summary>Executes a force/impulse/velocity command (see <see cref="PhysxForce"/>); kinds of the other dimension are ignored.</summary>
        /// <param name="force">Command to execute.</param>
        void ApplyForce(in PhysxForce force);
        /// <summary>Optional layer tag for query filtering; <see langword="null"/> behaves as <see cref="PhysxLayer.All"/>.</summary>
        PhysxTag? PhysxTag { get; set; }

        /// <summary>Finds an attached collider by id.</summary>
        /// <param name="colliderId">Collider id.</param>
        /// <param name="collider">The collider when found; otherwise <see langword="null"/>.</param>
        /// <returns><see langword="true"/> when found.</returns>
        bool TryGetColliderById(string colliderId, out IPhysxCollider collider);

        /// <summary>Returns the collider at <paramref name="index"/> in attach order, or <see langword="null"/> when out of range.</summary>
        /// <param name="index">Zero-based index.</param>
        IPhysxCollider? GetColliderAt(int index);
    }

    /// <summary>Minimal world contract shared by <c>IPhysxWorld2D</c> and <c>IPhysxWorld3D</c>.</summary>
    public interface IPhysxWorld
    {
        /// <summary>Advances the simulation by <paramref name="deltaTime"/> seconds (backends may sub-step at a fixed rate).</summary>
        /// <param name="deltaTime">Elapsed time in seconds.</param>
        void Step(float deltaTime);
    }


}
