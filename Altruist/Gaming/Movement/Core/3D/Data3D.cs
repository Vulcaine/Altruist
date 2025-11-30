using System.Numerics;

namespace Altruist.Gaming.Movement.ThreeD
{
    [Flags] public enum Planar3DFlags { None = 0, GroundPlane = 1 << 0, FreeFlight = 1 << 1 }
    [Flags] public enum Rotation3DFlags { None = 0, FaceAim = 1 << 0, FaceVelocity = 1 << 1, YawPitchRollRate = 1 << 2 }
    [Flags] public enum Dynamics3DFlags { None = 0, LinearAccel = 1 << 0, ExponentialDrag = 1 << 1, TractionCurve = 1 << 2 }
    [Flags] public enum Forces3DFlags { None = 0, Boost = 1 << 0, Dash = 1 << 1, Knockback = 1 << 2, Jump = 1 << 3 }

    /// <summary>
    /// Server-friendly intent: world move vec, signed yaw turn, optional aim direction, and jump.
    /// </summary>
    public sealed record MovementIntent3D(
        Vector3 Move,               // normalized [-1..1] (X=right, Y=up, Z=forward)
        float TurnYaw,              // -1..+1 yaw turn input (no camera required)
        bool Jump = false,
        Vector3 AimDirection = default, // optional if using aim-based modules
        bool Boost = false,
        bool Dash = false,
        Vector3 Knockback = default
    )
    {
        /// <summary>
        /// Create intent from digital buttons (Forward/Back/Left/Right [+ optional FlyUp/FlyDown]).
        /// Z is forward (Forward=+1), X is right (Right=+1), Y is up (FlyUp=+1).
        /// </summary>
        public static MovementIntent3D FromButtons(
            bool forward = false,
            bool back = false,
            bool left = false,
            bool right = false,
            bool flyUp = false,
            bool flyDown = false,
            float turnYaw = 0f,              // -1..+1 (Left=-1, Right=+1) if you prefer buttons map it before
            bool jump = false,
            bool boost = false,
            bool dash = false,
            Vector3? aimDirection = null,
            Vector3? knockback = null)
        {
            float x = right ? 1f : (left ? -1f : 0f);
            float z = forward ? 1f : (back ? -1f : 0f);
            float y = flyUp ? 1f : (flyDown ? -1f : 0f);

            var move = NormalizeClamped(new Vector3(x, y, z));
            return new MovementIntent3D(
                move,
                Clamp01Signed(turnYaw),
                jump,
                aimDirection ?? default,
                boost,
                dash,
                knockback ?? default
            );
        }

        public static MovementIntent3D Zero => new MovementIntent3D(Vector3.Zero, 0f);

        /// <summary>
        /// Create intent from analog axes: x=right, z=forward, y=up (for flight); turnYaw in [-1..+1].
        /// </summary>
        public static MovementIntent3D FromAxes(
            float x, float z, float y = 0f,
            float turnYaw = 0f,
            bool jump = false,
            bool boost = false,
            bool dash = false,
            Vector3? aimDirection = null,
            Vector3? knockback = null)
        {
            var move = NormalizeClamped(new Vector3(x, y, z));
            return new MovementIntent3D(
                move,
                Clamp01Signed(turnYaw),
                jump,
                aimDirection ?? default,
                boost,
                dash,
                knockback ?? default
            );
        }

        private static Vector3 NormalizeClamped(in Vector3 v)
        {
            var x = MathF.Max(-1f, MathF.Min(1f, v.X));
            var y = MathF.Max(-1f, MathF.Min(1f, v.Y));
            var z = MathF.Max(-1f, MathF.Min(1f, v.Z));
            var c = new Vector3(x, y, z);
            var len = c.Length();
            if (len < 1e-5f)
                return Vector3.Zero;
            return len > 1f ? c / len : c;
        }

        private static float Clamp01Signed(float v) => MathF.Max(-1f, MathF.Min(1f, v));
    }

    public sealed record MovementState3D(
        IPhysxBody3D Body,
        Vector3 Position,
        Vector3 Velocity,
        Quaternion Orientation
    );

    public sealed class MovementProfile3D
    {
        public float MaxSpeed { get; set; } = 10f;
        public float Acceleration { get; set; } = 30f;
        public float Deceleration { get; set; } = 30f;
        public float Drag { get; set; } = 0.1f;

        public float YawRate { get; set; } = 6f; // rad/sec
        public float PitchRate { get; set; } = 6f;
        public float RollRate { get; set; } = 6f;

        public float BoostMultiplier { get; set; } = 1.5f;
        public float DashSpeed { get; set; } = 18f;
        public float Traction { get; set; } = 1.0f; // 0..1
        public float Deadzone { get; set; } = 0.05f;

        // Jump tuning (used by ForceJump3D)
        public float JumpImpulse { get; set; } = 8.5f; // tweak for your units/engine
        public bool AllowAirJump { get; set; } = false; // if you add ground checks later

        // Optional snapping for direction modules (not used here)
        public float DirectionSnapRad { get; set; } = 0f;
    }

    public sealed record MovementResult3D(
        Vector3 LinearVelocity,
        Vector3 AngularDeltaEuler, // (pitch, yaw, roll) radians this tick
        Vector3 Force
    )
    {
        public static MovementResult3D Zero => new(Vector3.Zero, Vector3.Zero, Vector3.Zero);
    }

    public interface IPhysxMovementEngine3D
    {
        void Apply(object body, in MovementResult3D result, float dt);
    }
}
