using System.Numerics;

using Altruist.Physx.Contracts;
using Altruist.TwoD.Numerics;

namespace Altruist.Physx.TwoD
{
    /// <summary>Shape kind of an <see cref="IPhysxCollider2D"/>.</summary>
    public enum PhysxColliderShape2D
    {
        /// <summary>Circle (radius = <c>Transform.Size.X</c>).</summary>
        Circle2D,
        /// <summary>Axis box in collider space (half extents = <c>Transform.Size</c>).</summary>
        Box2D,
        /// <summary>Capsule (radius = <c>Size.X</c>, half length of the straight segment along local X =
        /// <c>Size.Y</c>); attached to a Box2D body as a box plus two circles.</summary>
        Capsule2D,
        /// <summary>Convex polygon (<see cref="IPhysxCollider2D.Vertices"/>), also non-box engine polygons.</summary>
        Polygon2D,
        /// <summary>Chain of one-sided edges (engine fixtures from <see cref="PhysxShape2D.Chain"/>; <see cref="IPhysxCollider2D.Vertices"/> holds the points).</summary>
        Chain2D,
        /// <summary>Single two-sided edge (engine fixtures from <see cref="PhysxShape2D.Edge"/>; <see cref="IPhysxCollider2D.Vertices"/> holds both ends).</summary>
        Edge2D,
    }


    /// <summary>
    /// A collision event as seen from one collider (the one whose event fired): "self" is that
    /// collider, "other" the one it hit. Raised by <see cref="IPhysxCollider2D.OnCollisionEnter"/>,
    /// <see cref="IPhysxCollider2D.OnCollisionStay"/> and <see cref="IPhysxCollider2D.OnCollisionExit"/>.
    /// </summary>
    public readonly struct PhysxCollisionInfo2D
    {
        /// <summary>The collider whose event fired.</summary>
        public IPhysxCollider2D SelfCollider { get; }

        /// <summary>The collider it touched.</summary>
        public IPhysxCollider2D OtherCollider { get; }

        /// <summary>The body owning <see cref="SelfCollider"/>.</summary>
        public IPhysxBody2D SelfBody { get; }

        /// <summary>The body owning <see cref="OtherCollider"/>.</summary>
        public IPhysxBody2D OtherBody { get; }

        /// <summary>Midpoint of the contact points (world); zero on exit.</summary>
        public Vector2 Point { get; }

        /// <summary>Unit contact normal pointing from self toward other (world); zero on exit.</summary>
        public Vector2 Normal { get; }

        /// <summary>Total normal impulse of the step's solve (mass × units/s; 0 on exit, or when the shapes only
        /// came within the contact margin).</summary>
        public float Impulse { get; }

        /// <summary>Creates the event data.</summary>
        /// <param name="selfCol">The collider whose event fired.</param>
        /// <param name="otherCol">The other collider.</param>
        /// <param name="selfBody">Body of <paramref name="selfCol"/>.</param>
        /// <param name="otherBody">Body of <paramref name="otherCol"/>.</param>
        /// <param name="point">Contact midpoint (world).</param>
        /// <param name="normal">Unit normal, self → other.</param>
        /// <param name="impulse">Total normal impulse.</param>
        public PhysxCollisionInfo2D(
            IPhysxCollider2D selfCol, IPhysxCollider2D otherCol,
            IPhysxBody2D selfBody, IPhysxBody2D otherBody,
            Vector2 point, Vector2 normal, float impulse)
        {
            SelfCollider = selfCol;
            OtherCollider = otherCol;
            SelfBody = selfBody;
            OtherBody = otherBody;
            Point = point;
            Normal = normal;
            Impulse = impulse;
        }
    }


    /// <summary>
    /// A 2D collider (shape) attached to a body, with per-collider collision events.
    /// <para>Event timing (Box2D engine fixtures): trigger enter/exit fire inside the step; collision
    /// enter fires after the step in which the shapes began touching (with that step's impulse), stay
    /// after every later step while touching, exit inside the step. All on the stepping thread.
    /// For pair-typed dispatch inside the step use <see cref="ContactRouter2D"/> instead.</para>
    /// </summary>
    public interface IPhysxCollider2D : IPhysxCollider
    {
        /// <summary>Local transform relative to the owning body.</summary>
        Transform2D Transform { get; set; }

        /// <summary>Shape discriminator.</summary>
        PhysxColliderShape2D Shape { get; }

        /// <summary>
        /// Optional vertex buffer for polygon colliders (ignored for other shapes).
        /// Return null for non-polygon shapes.
        /// </summary>
        Vector2[]? Vertices { get; }

        /// <summary>Raised once when this collider starts touching another non-sensor collider.</summary>
        event Action<PhysxCollisionInfo2D>? OnCollisionEnter;

        /// <summary>Raised after each later step while touching (not for sensors or contacts disabled
        /// in pre-solve). Subscribing enables a per-step contact walk in the Box2D engine.</summary>
        event Action<PhysxCollisionInfo2D>? OnCollisionStay;

        /// <summary>Raised when the touch ends (also when a body or fixture is removed); point, normal
        /// and impulse are zero.</summary>
        event Action<PhysxCollisionInfo2D>? OnCollisionExit;
    }


    /// <summary>A body-level ray hit (from <see cref="Altruist.Physx.IPhysxWorldEngine2D.RayCast(PhysxRay2D,int)"/>).</summary>
    public readonly struct PhysxRaycastHit2D
    {
        /// <summary>The body hit.</summary>
        public IPhysxBody2D Body { get; }

        /// <summary>World hit point.</summary>
        public Vector2 Point { get; }

        /// <summary>Unit surface normal at the hit (world).</summary>
        public Vector2 Normal { get; }

        /// <summary>Fraction (0..1) along the ray from <see cref="PhysxRay2D.From"/> to <see cref="PhysxRay2D.To"/>.</summary>
        public float Fraction { get; }

        /// <summary>Creates a hit.</summary>
        /// <param name="body">The body hit.</param>
        /// <param name="point">World hit point.</param>
        /// <param name="normal">Unit surface normal.</param>
        /// <param name="fraction">Fraction along the ray.</param>
        public PhysxRaycastHit2D(IPhysxBody2D body, Vector2 point, Vector2 normal, float fraction) { Body = body; Point = point; Normal = normal; Fraction = fraction; }
    }

    /// <summary>A ray segment from <see cref="From"/> to <see cref="To"/> (world).</summary>
    public readonly struct PhysxRay2D
    {
        /// <summary>Start point (world).</summary>
        public Vector2 From { get; }

        /// <summary>End point (world).</summary>
        public Vector2 To { get; }

        /// <summary>Creates a ray segment.</summary>
        /// <param name="from">Start point (world).</param>
        /// <param name="to">End point (world).</param>
        public PhysxRay2D(Vector2 from, Vector2 to) { From = from; To = to; }
    }

}
