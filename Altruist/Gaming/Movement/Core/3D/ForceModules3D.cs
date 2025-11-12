using System.Numerics;

namespace Altruist.Gaming.Movement.ThreeD
{
    /// <summary>
    /// Adds an upward impulse when intent.Jump is true. You can later gate with ground check.
    /// </summary>
    public sealed class ForceJump3D : IMoveModule3D
    {
        public void Execute(in MovementIntent3D intent, in MovementState3D state, MovementProfile3D profile, float dt, MoveContext3D ctx)
        {
            if (!intent.Jump)
                return;

            // Impulse as a force packet — let your PhysX adapter decide how to integrate it.
            ctx.Force += Vector3.UnitY * profile.JumpImpulse;
        }
    }

    // Forces
    internal sealed class ForceBoost3D : IMoveModule3D
    {
        public void Execute(in MovementIntent3D intent, in MovementState3D state, MovementProfile3D profile, float dt, MoveContext3D ctx)
        {
            if (!intent.Boost)
                return;
            var v = ctx.Velocity;
            if (v == Vector3.Zero && ctx.Desired != Vector3.Zero)
                v = Vector3.Normalize(ctx.Desired) * 0.01f;
            ctx.Velocity = v * profile.BoostMultiplier;
        }
    }

    internal sealed class ForceDash3D : IMoveModule3D
    {
        public void Execute(in MovementIntent3D intent, in MovementState3D state, MovementProfile3D profile, float dt, MoveContext3D ctx)
        {
            if (!intent.Dash)
                return;
            var dir = ctx.Desired != Vector3.Zero ? Vector3.Normalize(ctx.Desired)
                                                  : ForwardFrom(state.Orientation);
            ctx.Velocity = dir * profile.DashSpeed;
        }

        private static Vector3 ForwardFrom(Quaternion q)
        {
            var x2 = q.X + q.X;
            var y2 = q.Y + q.Y;
            var z2 = q.Z + q.Z;
            var xy = q.X * y2;
            var xz = q.X * z2;
            var yz = q.Y * z2;
            var wx = q.W * x2;
            var wy = q.W * y2;
            var wz = q.W * z2;
            return Vector3.Normalize(new Vector3(xz + wy, yz - wx, 1f - (q.X * x2 + q.Y * y2)));
        }
    }

    internal sealed class ForceKnockback3D : IMoveModule3D
    {
        public void Execute(in MovementIntent3D intent, in MovementState3D state, MovementProfile3D profile, float dt, MoveContext3D ctx)
        {
            if (intent.Knockback == Vector3.Zero)
                return;
            ctx.Force += intent.Knockback;
        }
    }

}
