// MovementDriver3D.cs
using System.Numerics;

namespace Altruist.Gaming.Movement.ThreeD
{
    public sealed class MovementDriver3D
    {
        public object Body { get; }
        public MovementProfile3D Profile { get; set; }
        public MovementState3D State { get; private set; }

        public IMovementPipeline3D Pipeline { get; set; }
        private readonly IPhysxMovementEngine3D _physx;

        public MovementDriver3D(
            object body,
            MovementProfile3D profile,
            MovementState3D initialState,
            IMovementPipeline3D pipeline,
            IPhysxMovementEngine3D physx)
        {
            Body = body;
            Profile = profile;
            Pipeline = pipeline;
            _physx = physx;
            State = initialState;
        }

        public void Step(in MovementIntent3D intent, float dt)
        {
            var result = Pipeline.Evaluate(intent, State, Profile, dt);

            _physx.Apply(Body, result, dt);

            var cur = State.Orientation;
            var yawAxis = Vector3.UnitY;

            var rightAxis = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, cur));
            var forwardAxis = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, cur));

            float pitchRad = result.AngularDeltaEuler.X;
            float yawRad = result.AngularDeltaEuler.Y;
            float rollRad = result.AngularDeltaEuler.Z;

            var yawQ = Quaternion.CreateFromAxisAngle(yawAxis, yawRad);
            var pitchQ = Quaternion.CreateFromAxisAngle(rightAxis, pitchRad);
            var rollQ = Quaternion.CreateFromAxisAngle(forwardAxis, rollRad);

            // Apply in a stable order:
            // yaw first (world), then pitch+roll in local axes
            var delta = Quaternion.Normalize(rollQ * pitchQ * yawQ);

            var nextOrientation = Quaternion.Normalize(delta * cur);

            // Validate orientation
            if (!IsFinite(nextOrientation))
            {
                // If it explodes, keep last orientation (don't corrupt state)
                nextOrientation = cur;
            }

            State = State with
            {
                Velocity = result.LinearVelocity,
                Orientation = nextOrientation
            };
        }

        static bool IsFinite(in Quaternion q)
        {
            return !(float.IsNaN(q.X) || float.IsNaN(q.Y) || float.IsNaN(q.Z) || float.IsNaN(q.W) ||
                     float.IsInfinity(q.X) || float.IsInfinity(q.Y) || float.IsInfinity(q.Z) || float.IsInfinity(q.W));
        }
    }
}
