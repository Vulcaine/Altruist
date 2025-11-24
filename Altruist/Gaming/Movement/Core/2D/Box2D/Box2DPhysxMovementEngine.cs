// Box2DPhysxMovementEngine2D.cs
using System.Numerics;

using Altruist.Physx.TwoD;

using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Movement.TwoD
{
    /// <summary>
    /// Adapts MovementResult2D from the movement pipeline to Box2D-backed bodies.
    /// - Sets linear velocity directly from the pipeline result.
    /// - Uses AngularDeltaRad to drive angular velocity.
    /// - Optionally uses Force as an impulse-like velocity tweak.
    /// </summary>
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "2D")]
    [Service(typeof(IPhysxMovementEngine2D))]
    internal sealed class Box2DPhysxMovementEngine2D : IPhysxMovementEngine2D
    {
        private readonly ILogger<Box2DPhysxMovementEngine2D> _log;

        public Box2DPhysxMovementEngine2D(ILogger<Box2DPhysxMovementEngine2D> log)
        {
            _log = log;
        }

        /// <summary>
        /// Apply the movement pipeline result to the underlying Box2D body.
        /// </summary>
        public void Apply(object body, in MovementResult2D result, float dt)
        {
            // Adjust this type to your actual Box2D adapter if needed.
            if (body is not Body2DAdapter boxBody)
            {
                throw new ArgumentException(
                    "Box2DPhysxMovementEngine2D expects a Box2D Body2DAdapter as the body.",
                    nameof(body));
            }

            // 1) Linear velocity: pipeline gives the desired velocity for this tick.
            boxBody.LinearVelocity = result.LinearVelocity;

            // 2) Rotation: MovementResult2D.AngularDeltaRad is an angle delta for this frame.
            //    Box2D works with angular velocity (rad/sec), so we convert:
            if (dt > 0f && result.AngularDeltaRad != 0f)
            {
                // ω ≈ Δθ / dt
                boxBody.AngularVelocityZ = result.AngularDeltaRad / dt;
            }

            // 3) Optional force: treat as a simple velocity tweak (dv ≈ F * dt).
            if (result.Force != Vector2.Zero && dt > 0f)
            {
                var dv = result.Force * dt;
                boxBody.LinearVelocity += dv;
            }
        }
    }
}
