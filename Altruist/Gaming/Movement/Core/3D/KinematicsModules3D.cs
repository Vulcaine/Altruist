namespace Altruist.Gaming.Movement.ThreeD
{
    // Kinematics
    internal sealed class KinematicsGround3D : IMoveModule3D
    {
        public void Execute(in MovementIntent3D intent, in MovementState3D state, MovementProfile3D profile, float dt, MoveContext3D ctx)
        {
            // Treat intent in world space; project on ground plane (Y up)
            var d = intent.Move;
            d.Y = 0f;
            ctx.Desired += d;
        }
    }

    internal sealed class KinematicsFreeFlight3D : IMoveModule3D
    {
        public void Execute(in MovementIntent3D intent, in MovementState3D state, MovementProfile3D profile, float dt, MoveContext3D ctx)
        {
            ctx.Desired += intent.Move; // full 3D
        }
    }

}