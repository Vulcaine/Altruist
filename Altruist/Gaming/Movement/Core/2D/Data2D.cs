using System.Numerics;

using Altruist.Physx.TwoD;

namespace Altruist.Gaming.Movement.TwoD
{
    // ------------------------------------------------------------
    // Public contracts (engine-agnostic)
    // ------------------------------------------------------------

    [Flags] public enum Planar2D { None = 0, ForwardOnly = 1 << 0, FreeStrafe = 1 << 1 }
    [Flags] public enum Rotation2D { None = 0, FaceAim = 1 << 0, FaceVelocity = 1 << 1, YawRate = 1 << 2 }
    [Flags] public enum Dynamics2D { None = 0, LinearAccel = 1 << 0, ExponentialDrag = 1 << 1, TractionCurve = 1 << 2 }
    [Flags] public enum Forces2D { None = 0, Boost = 1 << 0, Dash = 1 << 1, Knockback = 1 << 2, Jump = 1 << 3 }

    public sealed record MovementIntent2D(
        Vector2 Move,          // normalized [-1..1] strafe/forward input
        float AimAngleRad,     // radians, absolute aim (if used)
        bool Jump = false,
        bool Boost = false,
        bool Dash = false,
        Vector2 Knockback = default
    )
    {
        /// <summary>
        /// Create intent from digital buttons (Up/Down/Left/Right).
        /// Y is forward (Up=+1), X is right (Right=+1).
        /// </summary>
        public static MovementIntent2D FromButtons(
            bool up = false,
            bool down = false,
            bool left = false,
            bool right = false,
            float aimAngleRad = 0f,
            bool jump = false,
            bool boost = false,
            bool dash = false,
            Vector2? knockback = null)
        {
            float x = right ? 1f : (left ? -1f : 0f);
            float y = up ? 1f : (down ? -1f : 0f);
            var move = NormalizeClamped(new Vector2(x, y));
            return new MovementIntent2D(move, aimAngleRad, jump, boost, dash, knockback ?? default);
        }

        /// <summary>
        /// Create intent from analog axes (x = right, y = forward). Values are clamped to [-1..1].
        /// </summary>
        public static MovementIntent2D FromAxes(
            float x,
            float y,
            float aimAngleRad = 0f,
            bool jump = false,
            bool boost = false,
            bool dash = false,
            Vector2? knockback = null)
        {
            var move = NormalizeClamped(new Vector2(x, y));
            return new MovementIntent2D(move, aimAngleRad, jump, boost, dash, knockback ?? default);
        }

        private static Vector2 NormalizeClamped(in Vector2 v)
        {
            var x = MathF.Max(-1f, MathF.Min(1f, v.X));
            var y = MathF.Max(-1f, MathF.Min(1f, v.Y));
            var c = new Vector2(x, y);
            var len = c.Length();
            if (len < 1e-5f)
                return Vector2.Zero;
            return len > 1f ? c / len : c;
        }
    }

    public sealed record MovementState2D(
        IPhysxBody2D Body,
        Vector2 Position,
        Vector2 Velocity,
        float AngleRad
    );

    public sealed class MovementProfile2D
    {
        public float MaxSpeed { get; set; } = 10f;
        public float Acceleration { get; set; } = 30f;
        public float Deceleration { get; set; } = 30f;
        public float Drag { get; set; } = 0.1f;
        public float YawRate { get; set; } = 6f;           // rad/sec for YawRate mode

        // Optional: jumping knobs (pipeline may use these)
        public float JumpImpulse { get; set; } = 10f;      // world-units/sec impulse magnitude
        public bool AllowAirJumps { get; set; } = false;   // if you implement coyote or multi-jumps in pipeline

        public float BoostMultiplier { get; set; } = 1.5f;
        public float DashSpeed { get; set; } = 18f;
        public float Traction { get; set; } = 1.0f;        // 0..1 curve factor
        public float Deadzone { get; set; } = 0.05f;
        public float GridSnap { get; set; } = 0f;          // 0 -> off; else world-unit snap
    }

    public sealed record MovementResult2D(
        Vector2 LinearVelocity,
        float AngularDeltaRad, // delta angle (add this * dt at apply-time) if you want smooth yaw
        Vector2 Force          // optional world force to apply (e.g., jump impulse along +Y)
    )
    {
        public static MovementResult2D Zero => new(Vector2.Zero, 0f, Vector2.Zero);
    }

    // The only place that touches your physics engine implementation.
    public interface IPhysxMovementEngine2D
    {
        void Apply(object body, in MovementResult2D result, float dt);
    }
}
