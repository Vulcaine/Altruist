// Movement3D.cs — pipeline & modules
using System.Numerics;

namespace Altruist.Gaming.Movement.ThreeD
{

    public interface IMovementPipeline3D
    {
        MovementResult3D Evaluate(in MovementIntent3D intent, in MovementState3D state, MovementProfile3D profile, float dt);
    }

    // ------------------------------------------------------------
    // Builder
    // ------------------------------------------------------------

    public sealed class MovementBuilder3D
    {
        private Planar3D _kin = Planar3D.GroundPlane;
        private Rotation3D _rot = Rotation3D.YawPitchRollRate;
        private Dynamics3D _dyn = Dynamics3D.LinearAccel | Dynamics3D.ExponentialDrag;
        private Forces3D _forces = Forces3D.None;
        private readonly List<Action<MovementProfile3D>> _constraintTweaks = new();

        public MovementBuilder3D WithKinematics(Planar3D flags) { _kin = flags; return this; }
        public MovementBuilder3D WithRotation(Rotation3D flags) { _rot = flags; return this; }
        public MovementBuilder3D WithDynamics(Dynamics3D flags) { _dyn = flags; return this; }
        public MovementBuilder3D WithForces(Forces3D flags) { _forces = flags; return this; }
        public MovementBuilder3D WithConstraints(params Action<MovementProfile3D>[] cfg) { _constraintTweaks.AddRange(cfg); return this; }

        public IMovementPipeline3D Build()
        {
            var modules = new List<IMoveModule3D>
            {
                new DeadzoneConstraintModule3D(),
                new DirectionSnapConstraintModule3D()
            };

            // Kinematics
            if (_kin.HasFlag(Planar3D.GroundPlane)) modules.Add(new KinematicsGround3D());
            if (_kin.HasFlag(Planar3D.FreeFlight)) modules.Add(new KinematicsFreeFlight3D());

            // Rotation
            if (_rot.HasFlag(Rotation3D.FaceAim)) modules.Add(new RotationFaceAim3D());
            if (_rot.HasFlag(Rotation3D.FaceVelocity)) modules.Add(new RotationFaceVelocity3D());
            if (_rot.HasFlag(Rotation3D.YawPitchRollRate)) modules.Add(new RotationYPRRate3D());

            // Dynamics
            if (_dyn.HasFlag(Dynamics3D.LinearAccel)) modules.Add(new DynamicsLinearAccel3D());
            if (_dyn.HasFlag(Dynamics3D.ExponentialDrag)) modules.Add(new DynamicsDrag3D());
            if (_dyn.HasFlag(Dynamics3D.TractionCurve)) modules.Add(new DynamicsTraction3D());

            // Forces
            if (_forces.HasFlag(Forces3D.Boost)) modules.Add(new ForceBoost3D());
            if (_forces.HasFlag(Forces3D.Dash)) modules.Add(new ForceDash3D());
            if (_forces.HasFlag(Forces3D.Knockback)) modules.Add(new ForceKnockback3D());

            var tweaks = _constraintTweaks.ToArray();
            return new MovementPipeline3D(modules, tweaks);
        }
    }

    // ------------------------------------------------------------
    // Pipeline + module protocol
    // ------------------------------------------------------------

    public sealed class MoveContext3D
    {
        public Vector3 Desired;         // desired direction (world)
        public Vector3 Velocity;        // working velocity
        public Quaternion Target;       // desired orientation
        public Vector3 AngularDelta;    // (pitch, yaw, roll) deltas
        public Vector3 Force;           // cumulative force
    }

    public interface IMoveModule3D
    {
        void Execute(in MovementIntent3D intent, in MovementState3D state, MovementProfile3D profile, float dt, MoveContext3D ctx);
    }

    internal sealed class MovementPipeline3D : IMovementPipeline3D
    {
        private readonly IMoveModule3D[] _modules;
        private readonly Action<MovementProfile3D>[] _tweaks;

        public MovementPipeline3D(IEnumerable<IMoveModule3D> modules, Action<MovementProfile3D>[] tweaks)
        {
            _modules = modules.ToArray();
            _tweaks = tweaks;
        }

        public MovementResult3D Evaluate(in MovementIntent3D intent, in MovementState3D state, MovementProfile3D profile, float dt)
        {
            foreach (var t in _tweaks) t(profile);

            var ctx = new MoveContext3D
            {
                Desired = Vector3.Zero,
                Velocity = state.Velocity,
                Target = state.Orientation,
                AngularDelta = Vector3.Zero,
                Force = Vector3.Zero
            };

            for (int i = 0; i < _modules.Length; i++)
                _modules[i].Execute(intent, state, profile, dt, ctx);

            return new MovementResult3D(ctx.Velocity, ctx.AngularDelta, ctx.Force);
        }
    }

    // ------------------------------------------------------------
    // PhysX-facing contracts for 3D bodies (parallel to 2D)
    // ------------------------------------------------------------

    public interface IPhysxBody3D { /* marker/type owned by your PhysX layer */ }
}
