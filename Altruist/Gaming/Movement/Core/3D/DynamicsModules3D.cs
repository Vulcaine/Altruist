using System.Numerics;

namespace Altruist.Gaming.Movement.ThreeD
{
    // Dynamics
    internal sealed class DynamicsLinearAccel3D : IMoveModule3D
    {
        public void Execute(in MovementIntent3D intent, in MovementState3D state, MovementProfile3D profile, float dt, MoveContext3D ctx)
        {
            var desiredVel = ctx.Desired;
            if (desiredVel != Vector3.Zero)
                desiredVel = Vector3.Normalize(desiredVel) * profile.MaxSpeed;

            var current = ctx.Velocity;
            var diff = desiredVel - current;

            var accel = diff;
            var maxAccel = (Vector3.Dot(diff, desiredVel) >= 0 ? profile.Acceleration : profile.Deceleration) * dt;
            var len = accel.Length();
            if (len > maxAccel && len > 1e-5f)
                accel = accel * (maxAccel / len);

            ctx.Velocity = current + accel;
        }
    }

    internal sealed class DynamicsDrag3D : IMoveModule3D
    {
        public void Execute(in MovementIntent3D intent, in MovementState3D state, MovementProfile3D profile, float dt, MoveContext3D ctx)
        {
            var drag = MathF.Exp(-profile.Drag * dt);
            ctx.Velocity *= drag;
        }
    }

    internal sealed class DynamicsTraction3D : IMoveModule3D
    {
        public void Execute(in MovementIntent3D intent, in MovementState3D state, MovementProfile3D profile, float dt, MoveContext3D ctx)
        {
            if (ctx.Desired == Vector3.Zero || ctx.Velocity == Vector3.Zero)
                return;

            var vDir = Vector3.Normalize(ctx.Velocity);
            var dDir = Vector3.Normalize(ctx.Desired);
            var blend = Math.Clamp(profile.Traction, 0f, 1f);
            var newDir = Vector3.Normalize(Vector3.Lerp(vDir, dDir, blend));
            var speed = ctx.Velocity.Length();
            ctx.Velocity = newDir * speed;
        }
    }
}
