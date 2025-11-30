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
        private Planar3DFlags _kin = Planar3DFlags.GroundPlane;
        private Rotation3DFlags _rot = Rotation3DFlags.YawPitchRollRate;
        private Dynamics3DFlags _dyn = Dynamics3DFlags.LinearAccel | Dynamics3DFlags.ExponentialDrag;
        private Forces3DFlags _forces = Forces3DFlags.None;
        private readonly List<Action<MovementProfile3D>> _constraintTweaks = new();

        public MovementBuilder3D WithKinematics(Planar3DFlags flags) { _kin = flags; return this; }
        public MovementBuilder3D WithRotation(Rotation3DFlags flags) { _rot = flags; return this; }
        public MovementBuilder3D WithDynamics(Dynamics3DFlags flags) { _dyn = flags; return this; }
        public MovementBuilder3D WithForces(Forces3DFlags flags) { _forces = flags; return this; }
        public MovementBuilder3D WithConstraints(params Action<MovementProfile3D>[] cfg) { _constraintTweaks.AddRange(cfg); return this; }

        public IMovementPipeline3D Build()
        {
            var modules = new List<IMoveModule3D>
            {
                new DeadzoneConstraintModule3D(),
                new DirectionSnapConstraintModule3D()
            };

            // Kinematics
            if (_kin.HasFlag(Planar3DFlags.GroundPlane))
                modules.Add(new KinematicsGround3D());
            if (_kin.HasFlag(Planar3DFlags.FreeFlight))
                modules.Add(new KinematicsFreeFlight3D());

            // Rotation
            if (_rot.HasFlag(Rotation3DFlags.FaceAim))
                modules.Add(new RotationFaceAim3D());
            if (_rot.HasFlag(Rotation3DFlags.FaceVelocity))
                modules.Add(new RotationFaceVelocity3D());
            if (_rot.HasFlag(Rotation3DFlags.YawPitchRollRate))
                modules.Add(new RotationYPRRate3D());

            // Dynamics
            if (_dyn.HasFlag(Dynamics3DFlags.LinearAccel))
                modules.Add(new DynamicsLinearAccel3D());
            if (_dyn.HasFlag(Dynamics3DFlags.ExponentialDrag))
                modules.Add(new DynamicsDrag3D());
            if (_dyn.HasFlag(Dynamics3DFlags.TractionCurve))
                modules.Add(new DynamicsTraction3D());

            // Forces
            if (_forces.HasFlag(Forces3DFlags.Boost))
                modules.Add(new ForceBoost3D());
            if (_forces.HasFlag(Forces3DFlags.Dash))
                modules.Add(new ForceDash3D());
            if (_forces.HasFlag(Forces3DFlags.Knockback))
                modules.Add(new ForceKnockback3D());

            var tweaks = _constraintTweaks.ToArray();
            return new MovementPipeline3D(modules, tweaks);
        }
    }

    // ------------------------------------------------------------
    // Pipeline + module protocol
    // ------------------------------------------------------------

    public sealed class MoveContext3D
    {
        public Vector3 Desired;
        public Vector3 Velocity;
        public Quaternion Target;
        public Vector3 AngularDelta;
        public Vector3 Force;
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
            foreach (var t in _tweaks)
                t(profile);

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
