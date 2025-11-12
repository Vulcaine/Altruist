using System.Numerics;

namespace Altruist.Gaming.Movement.ThreeD
{
    // Constraints
    internal sealed class DeadzoneConstraintModule3D : IMoveModule3D
    {
        public void Execute(in MovementIntent3D intent, in MovementState3D state, MovementProfile3D profile, float dt, MoveContext3D ctx)
        {
            var move = intent.Move;
            if (move.LengthSquared() < profile.Deadzone * profile.Deadzone)
                move = Vector3.Zero;
            ctx.Desired = move;
        }
    }

    internal sealed class DirectionSnapConstraintModule3D : IMoveModule3D
    {
        public void Execute(in MovementIntent3D intent, in MovementState3D state, MovementProfile3D profile, float dt, MoveContext3D ctx)
        {
            if (profile.DirectionSnapRad <= 0f || ctx.Desired == Vector3.Zero)
                return;

            // Snap azimuth around Y and elevation from XZ-plane
            var dir = Vector3.Normalize(ctx.Desired);
            var az = MathF.Atan2(dir.X, dir.Z);            // yaw around Y
            var el = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f)); // pitch

            float snap = profile.DirectionSnapRad;
            az = MathF.Round(az / snap) * snap;
            el = MathF.Round(el / snap) * snap;

            var cosEl = MathF.Cos(el);
            ctx.Desired = Vector3.Normalize(new Vector3(
                cosEl * MathF.Sin(az),
                MathF.Sin(el),
                cosEl * MathF.Cos(az)
            ));
        }
    }
}
