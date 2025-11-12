using System.Numerics;

namespace Altruist.Gaming.Movement.ThreeD
{
    public sealed class RotationYawInput3D : IMoveModule3D
    {
        public void Execute(in MovementIntent3D intent, in MovementState3D state, MovementProfile3D profile, float dt, MoveContext3D ctx)
        {
            // Only yaw about up-axis; pitch/roll handled by other modules if used
            ctx.AngularDelta += new Vector3(0f, intent.TurnYaw * profile.YawRate * dt, 0f);
        }
    }
    // Rotation
    internal sealed class RotationFaceAim3D : IMoveModule3D
    {
        public void Execute(in MovementIntent3D intent, in MovementState3D state, MovementProfile3D profile, float dt, MoveContext3D ctx)
        {
            if (intent.AimDirection == Vector3.Zero)
                return;
            var forward = Vector3.Normalize(intent.AimDirection);
            var up = Vector3.UnitY;
            ctx.Target = LookRotation(forward, up);

            // Compute Euler delta from current to target (approx via axis-angle)
            var deltaQ = ctx.Target * Quaternion.Inverse(state.Orientation);
            deltaQ = Quaternion.Normalize(deltaQ);
            ToApproxEuler(deltaQ, out var pitch, out var yaw, out var roll);
            ctx.AngularDelta += new Vector3(pitch, yaw, roll);
        }

        private static Quaternion LookRotation(Vector3 forward, Vector3 up)
        {
            forward = Vector3.Normalize(forward);
            up = Vector3.Normalize(up);
            var right = Vector3.Normalize(Vector3.Cross(up, forward));
            up = Vector3.Cross(forward, right);

            var m = new Matrix4x4(
                right.X, right.Y, right.Z, 0,
                up.X, up.Y, up.Z, 0,
                forward.X, forward.Y, forward.Z, 0,
                0, 0, 0, 1);

            return Quaternion.CreateFromRotationMatrix(m);
        }

        // Small-angle approximation to Euler (pitch,x) (yaw,y) (roll,z)
        internal static void ToApproxEuler(Quaternion q, out float pitch, out float yaw, out float roll)
        {
            // Using Tait–Bryan YXZ-ish small angle conversions
            var sinr_cosp = 2f * (q.W * q.X + q.Y * q.Z);
            var cosr_cosp = 1f - 2f * (q.X * q.X + q.Y * q.Y);
            roll = MathF.Atan2(sinr_cosp, cosr_cosp);

            var sinp = 2f * (q.W * q.Y - q.Z * q.X);
            pitch = MathF.Abs(sinp) >= 1 ? MathF.CopySign(MathF.PI / 2f, sinp) : MathF.Asin(sinp);

            var siny_cosp = 2f * (q.W * q.Z + q.X * q.Y);
            var cosy_cosp = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
            yaw = MathF.Atan2(siny_cosp, cosy_cosp);
        }
    }

    internal sealed class RotationFaceVelocity3D : IMoveModule3D
    {
        public void Execute(in MovementIntent3D intent, in MovementState3D state, MovementProfile3D profile, float dt, MoveContext3D ctx)
        {
            var v = ctx.Velocity;
            if (v.LengthSquared() < 1e-4f && ctx.Desired.LengthSquared() > 1e-6f)
                v = ctx.Desired;
            if (v.LengthSquared() < 1e-6f)
                return;

            var target = RotationFaceAim3D_Look(v);
            var deltaQ = target * Quaternion.Inverse(state.Orientation);
            deltaQ = Quaternion.Normalize(deltaQ);
            RotationFaceAim3D.ToApproxEuler(deltaQ, out var pitch, out var yaw, out var roll);
            ctx.AngularDelta += new Vector3(pitch, yaw, roll);

            static Quaternion RotationFaceAim3D_Look(Vector3 forward)
            {
                forward = Vector3.Normalize(forward);
                var up = Vector3.UnitY;
                var right = Vector3.Normalize(Vector3.Cross(up, forward));
                up = Vector3.Cross(forward, right);

                var m = new Matrix4x4(
                    right.X, right.Y, right.Z, 0,
                    up.X, up.Y, up.Z, 0,
                    forward.X, forward.Y, forward.Z, 0,
                    0, 0, 0, 1);

                return Quaternion.CreateFromRotationMatrix(m);
            }
        }
    }

    internal sealed class RotationYPRRate3D : IMoveModule3D
    {
        public void Execute(in MovementIntent3D intent, in MovementState3D state, MovementProfile3D profile, float dt, MoveContext3D ctx)
        {
            var max = new Vector3(profile.PitchRate * dt, profile.YawRate * dt, profile.RollRate * dt);
            ctx.AngularDelta = ClampEuler(ctx.AngularDelta, max);
        }

        private static Vector3 ClampEuler(in Vector3 v, in Vector3 max)
            => new(
                MathF.Max(MathF.Min(v.X, max.X), -max.X),
                MathF.Max(MathF.Min(v.Y, max.Y), -max.Y),
                MathF.Max(MathF.Min(v.Z, max.Z), -max.Z)
            );
    }
}
