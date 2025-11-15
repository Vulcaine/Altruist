// BepuPhysxMovementEngine3D.cs
using System.Numerics;

using Altruist.Physx.ThreeD;

using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Movement.ThreeD
{
    /// <summary>
    /// Adapts MovementResult3D coming from the movement pipeline to BEPU bodies.
    /// - Sets linear velocity directly from the pipeline result.
    /// - Applies orientation delta as an immediate quaternion rotation.
    /// - Optionally uses the Force vector as an impulse-like tweak.
    /// </summary>
    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "3D")]
    [Service(typeof(IPhysxMovementEngine3D))]
    internal sealed class Bepu3DPhysxMovementEngine : IPhysxMovementEngine3D
    {
        private readonly ILogger<Bepu3DPhysxMovementEngine> _log;

        public Bepu3DPhysxMovementEngine(ILogger<Bepu3DPhysxMovementEngine> log)
        {
            _log = log;
        }

        /// <summary>
        /// Apply the movement pipeline result to the underlying BEPU body.
        /// </summary>
        public void Apply(object body, in MovementResult3D result, float dt)
        {
            if (body is not BepuWorldEngine3D.Body3DAdapter bepuBody)
            {
                throw new ArgumentException(
                    "BepuPhysxMovementEngine3D expects a BEPU Body3DAdapter as the body.", nameof(body));
            }

            bepuBody.LinearVelocity = result.LinearVelocity;
            if (result.AngularDeltaEuler != Vector3.Zero)
            {
                var current = bepuBody.Rotation;
                var deltaQ = Quaternion.CreateFromYawPitchRoll(
                    result.AngularDeltaEuler.Y,
                    result.AngularDeltaEuler.X,
                    result.AngularDeltaEuler.Z);

                bepuBody.Rotation = Quaternion.Normalize(deltaQ * current);
            }

            if (result.Force != Vector3.Zero && dt > 0f)
            {
                var dv = result.Force * dt;
                bepuBody.LinearVelocity += dv;
            }
        }
    }
}
