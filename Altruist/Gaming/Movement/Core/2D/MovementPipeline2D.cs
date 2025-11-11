/* 2D Movement Pipeline — box2d-agnostic, composable API
 * Build behaviors like:
 *   new MovementBuilder2D()
 *     .WithKinematics(Planar2D.ForwardOnly | Planar2D.FreeStrafe)
 *     .WithRotation(Rotation2D.FaceAim | Rotation2D.FaceVelocity | Rotation2D.YawRate)
 *     .WithDynamics(Dynamics2D.LinearAccel | Dynamics2D.ExponentialDrag | Dynamics2D.TractionCurve)
 *     .WithConstraints(c => c.MaxSpeed = 15, c => c.Deadzone = 0.1f)
 *     .WithForces(Forces2D.Boost | Forces2D.Dash | Forces2D.Knockback)
 *     .Build();
 *
 * Then each tick:
 *   var result = pipeline.Evaluate(intent, state, profile, dt);
 *   physx.Apply(state.Body, result, dt);
 */

using System.Numerics;
using Altruist.Physx.TwoD;

namespace Altruist.Gaming.Movement.TwoD
{


    public interface IMovementPipeline2D
    {
        MovementResult2D Evaluate(in MovementIntent2D intent, in MovementState2D state, MovementProfile2D profile, float dt);
    }

    // ------------------------------------------------------------
    // Builder: chooses modules based on flags, stays agnostic
    // ------------------------------------------------------------

    public sealed class MovementBuilder2D
    {
        private Planar2D _kin = Planar2D.ForwardOnly;
        private Rotation2D _rot = Rotation2D.YawRate;
        private Dynamics2D _dyn = Dynamics2D.LinearAccel | Dynamics2D.ExponentialDrag;
        private Forces2D _forces = Forces2D.None;
        private readonly List<Action<MovementProfile2D>> _constraintTweaks = new();

        public MovementBuilder2D WithKinematics(Planar2D flags) { _kin = flags; return this; }
        public MovementBuilder2D WithRotation(Rotation2D flags) { _rot = flags; return this; }
        public MovementBuilder2D WithDynamics(Dynamics2D flags) { _dyn = flags; return this; }
        public MovementBuilder2D WithForces(Forces2D flags) { _forces = flags; return this; }
        public MovementBuilder2D WithConstraints(params Action<MovementProfile2D>[] cfg) { _constraintTweaks.AddRange(cfg); return this; }

        public IMovementPipeline2D Build()
        {
            var modules = new List<IMoveModule2D>
            {
                new DeadzoneConstraintModule2D(),              // constraints first
                new GridSnapConstraintModule2D()
            };

            // Kinematics
            if (_kin.HasFlag(Planar2D.ForwardOnly)) modules.Add(new KinematicsForwardOnly2D());
            if (_kin.HasFlag(Planar2D.FreeStrafe)) modules.Add(new KinematicsFreeStrafe2D());

            // Rotation
            if (_rot.HasFlag(Rotation2D.FaceAim)) modules.Add(new RotationFaceAim2D());
            if (_rot.HasFlag(Rotation2D.FaceVelocity)) modules.Add(new RotationFaceVelocity2D());
            if (_rot.HasFlag(Rotation2D.YawRate)) modules.Add(new RotationYawRate2D());

            // Dynamics
            if (_dyn.HasFlag(Dynamics2D.LinearAccel)) modules.Add(new DynamicsLinearAccel2D());
            if (_dyn.HasFlag(Dynamics2D.ExponentialDrag)) modules.Add(new DynamicsDrag2D());
            if (_dyn.HasFlag(Dynamics2D.TractionCurve)) modules.Add(new DynamicsTraction2D());

            // Forces
            if (_forces.HasFlag(Forces2D.Boost)) modules.Add(new ForceBoost2D());
            if (_forces.HasFlag(Forces2D.Dash)) modules.Add(new ForceDash2D());
            if (_forces.HasFlag(Forces2D.Knockback)) modules.Add(new ForceKnockback2D());

            // Compose into a single pipeline
            var tweaks = _constraintTweaks.ToArray();
            return new MovementPipeline2D(modules, tweaks);
        }
    }

    // ------------------------------------------------------------
    // Pipeline + module protocol
    // ------------------------------------------------------------

    // Scratch data passed between modules (mutable for perf, still engine-agnostic)
    public sealed class MoveContext2D
    {
        public Vector2 Desired;          // what we want to move toward (world or local resolved)
        public Vector2 Velocity;         // working velocity
        public float TargetAngle;        // optional target orientation
        public float AngularDelta;       // how much to turn this tick (rad)
        public Vector2 Force;            // cumulative force
    }

    public interface IMoveModule2D
    {
        void Execute(in MovementIntent2D intent, in MovementState2D state, MovementProfile2D profile, float dt, MoveContext2D ctx);
    }

    internal sealed class MovementPipeline2D : IMovementPipeline2D
    {
        private readonly IMoveModule2D[] _modules;
        private readonly Action<MovementProfile2D>[] _tweaks;

        public MovementPipeline2D(IEnumerable<IMoveModule2D> modules, Action<MovementProfile2D>[] tweaks)
        {
            _modules = modules.ToArray();
            _tweaks = tweaks;
        }

        public MovementResult2D Evaluate(in MovementIntent2D intent, in MovementState2D state, MovementProfile2D profile, float dt)
        {
            // Apply constraint tweaks configured on the builder (idempotent if your profiles are per-entity)
            foreach (var t in _tweaks) t(profile);

            var ctx = new MoveContext2D { Desired = Vector2.Zero, Velocity = state.Velocity, TargetAngle = state.AngleRad, AngularDelta = 0f, Force = Vector2.Zero };
            for (int i = 0; i < _modules.Length; i++) _modules[i].Execute(intent, state, profile, dt, ctx);
            return new MovementResult2D(ctx.Velocity, ctx.AngularDelta, ctx.Force);
        }
    }

    // ------------------------------------------------------------
    // Built-in modules (minimal, generic math only)
    // ------------------------------------------------------------

    // Constraints
    internal sealed class DeadzoneConstraintModule2D : IMoveModule2D
    {
        public void Execute(in MovementIntent2D intent, in MovementState2D state, MovementProfile2D profile, float dt, MoveContext2D ctx)
        {
            var move = intent.Move;
            if (move.LengthSquared() < profile.Deadzone * profile.Deadzone) move = Vector2.Zero;
            ctx.Desired = move;
        }
    }

    internal sealed class GridSnapConstraintModule2D : IMoveModule2D
    {
        public void Execute(in MovementIntent2D intent, in MovementState2D state, MovementProfile2D profile, float dt, MoveContext2D ctx)
        {
            if (profile.GridSnap <= 0f || ctx.Desired == Vector2.Zero) return;
            var dir = ctx.Desired;
            var angle = MathF.Atan2(dir.Y, dir.X);
            var step = profile.GridSnap;
            var snapped = MathF.Round(angle / step) * step;
            ctx.Desired = new Vector2(MathF.Cos(snapped), MathF.Sin(snapped));
        }
    }

    // Kinematics
    internal sealed class KinematicsForwardOnly2D : IMoveModule2D
    {
        public void Execute(in MovementIntent2D intent, in MovementState2D state, MovementProfile2D profile, float dt, MoveContext2D ctx)
        {
            // Use Y component as forward throttle in local facing
            var throttle = intent.Move.Y;
            if (MathF.Abs(throttle) < profile.Deadzone) throttle = 0f;
            var forward = new Vector2(MathF.Cos(state.AngleRad), MathF.Sin(state.AngleRad));
            ctx.Desired += forward * throttle;
        }
    }

    internal sealed class KinematicsFreeStrafe2D : IMoveModule2D
    {
        public void Execute(in MovementIntent2D intent, in MovementState2D state, MovementProfile2D profile, float dt, MoveContext2D ctx)
        {
            // Add planar strafing (world space)
            ctx.Desired += intent.Move;
        }
    }

    // Rotation
    internal sealed class RotationFaceAim2D : IMoveModule2D
    {
        public void Execute(in MovementIntent2D intent, in MovementState2D state, MovementProfile2D profile, float dt, MoveContext2D ctx)
        {
            ctx.TargetAngle = intent.AimAngleRad;
            var diff = NormalizeAngle(ctx.TargetAngle - state.AngleRad);
            ctx.AngularDelta += diff; // let yaw module clamp with rate if also present
        }
        private static float NormalizeAngle(float a) { while (a > MathF.PI) a -= 2 * MathF.PI; while (a < -MathF.PI) a += 2 * MathF.PI; return a; }
    }

    internal sealed class RotationFaceVelocity2D : IMoveModule2D
    {
        public void Execute(in MovementIntent2D intent, in MovementState2D state, MovementProfile2D profile, float dt, MoveContext2D ctx)
        {
            var v = ctx.Velocity;
            if (v.LengthSquared() < 1e-4f && ctx.Desired.LengthSquared() > 1e-6f) v = ctx.Desired;
            if (v.LengthSquared() < 1e-6f) return;
            var angle = MathF.Atan2(v.Y, v.X);
            var diff = angle - state.AngleRad;
            ctx.AngularDelta += NormalizeAngle(diff);
            static float NormalizeAngle(float a) { while (a > MathF.PI) a -= 2 * MathF.PI; while (a < -MathF.PI) a += 2 * MathF.PI; return a; }
        }
    }

    internal sealed class RotationYawRate2D : IMoveModule2D
    {
        public void Execute(in MovementIntent2D intent, in MovementState2D state, MovementProfile2D profile, float dt, MoveContext2D ctx)
        {
            // Clamp turning by YawRate if another module requested rotation
            var maxDelta = profile.YawRate * dt;
            ctx.AngularDelta = MathF.Max(MathF.Min(ctx.AngularDelta, maxDelta), -maxDelta);
        }
    }

    // Dynamics
    internal sealed class DynamicsLinearAccel2D : IMoveModule2D
    {
        public void Execute(in MovementIntent2D intent, in MovementState2D state, MovementProfile2D profile, float dt, MoveContext2D ctx)
        {
            var desiredVel = ctx.Desired;
            if (desiredVel != Vector2.Zero) desiredVel = Vector2.Normalize(desiredVel) * profile.MaxSpeed;
            var current = ctx.Velocity;
            var diff = desiredVel - current;
            var accel = diff;
            var maxAccel = (Vector2.Dot(diff, desiredVel) >= 0 ? profile.Acceleration : profile.Deceleration) * dt;
            var len = accel.Length();
            if (len > maxAccel && len > 1e-5f) accel = accel * (maxAccel / len);
            ctx.Velocity = current + accel;
        }
    }

    internal sealed class DynamicsDrag2D : IMoveModule2D
    {
        public void Execute(in MovementIntent2D intent, in MovementState2D state, MovementProfile2D profile, float dt, MoveContext2D ctx)
        {
            var drag = MathF.Exp(-profile.Drag * dt);
            ctx.Velocity *= drag;
        }
    }

    internal sealed class DynamicsTraction2D : IMoveModule2D
    {
        public void Execute(in MovementIntent2D intent, in MovementState2D state, MovementProfile2D profile, float dt, MoveContext2D ctx)
        {
            // Simple blend towards desired direction to simulate traction
            if (ctx.Desired == Vector2.Zero || ctx.Velocity == Vector2.Zero) return;
            var vDir = Vector2.Normalize(ctx.Velocity);
            var dDir = Vector2.Normalize(ctx.Desired);
            var blend = Math.Clamp(profile.Traction, 0f, 1f);
            var newDir = Vector2.Normalize(Vector2.Lerp(vDir, dDir, blend));
            var speed = ctx.Velocity.Length();
            ctx.Velocity = newDir * speed;
        }
    }

    // Forces
    internal sealed class ForceBoost2D : IMoveModule2D
    {
        public void Execute(in MovementIntent2D intent, in MovementState2D state, MovementProfile2D profile, float dt, MoveContext2D ctx)
        {
            if (!intent.Boost) return;
            var v = ctx.Velocity;
            if (v == Vector2.Zero && ctx.Desired != Vector2.Zero) v = Vector2.Normalize(ctx.Desired) * 0.01f;
            ctx.Velocity = v * profile.BoostMultiplier;
        }
    }

    internal sealed class ForceDash2D : IMoveModule2D
    {
        public void Execute(in MovementIntent2D intent, in MovementState2D state, MovementProfile2D profile, float dt, MoveContext2D ctx)
        {
            if (!intent.Dash) return;
            var dir = ctx.Desired != Vector2.Zero ? Vector2.Normalize(ctx.Desired) :
                      new Vector2(MathF.Cos(state.AngleRad), MathF.Sin(state.AngleRad));
            ctx.Velocity = dir * profile.DashSpeed;
        }
    }

    internal sealed class ForceKnockback2D : IMoveModule2D
    {
        public void Execute(in MovementIntent2D intent, in MovementState2D state, MovementProfile2D profile, float dt, MoveContext2D ctx)
        {
            if (intent.Knockback == Vector2.Zero) return;
            ctx.Force += intent.Knockback; // leave integration to physx engine
        }
    }
}
