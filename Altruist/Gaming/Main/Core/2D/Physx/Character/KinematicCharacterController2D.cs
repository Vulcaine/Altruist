/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Physx.TwoD;

namespace Altruist.Gaming.TwoD
{
    public interface IKinematicCharacterController2D
    {
        void SetBody(IPhysxBody2D body);

        /// <summary>Horizontal movement intent (-1 to 1 normalized).</summary>
        void MoveIntent(float moveX);

        /// <summary>Look direction intent (for facing).</summary>
        void LookIntent(float lookX, float lookY);

        void SprintIntent(bool sprintHeld);
        void JumpIntent(bool jumpPressed);

        void Step(float dt, IGameWorldManager2D world);

        // State outputs
        Vector2 Position { get; }
        float RotationZ { get; }
        bool IsGrounded { get; }
        Vector2 Velocity { get; }
    }

    public interface ICharacterAbility2D
    {
        void Step(float dt, ref CharacterMotorContext2D ctx);
    }

    public struct CharacterMotorContext2D
    {
        public IPhysxBody2D Body;
        public IGameWorldManager2D World;

        public float DesiredMoveX;   // -1..1 normalized horizontal direction
        public float DesiredSpeed;   // m/s

        public Vector2 Velocity;
        public bool IsGrounded;
        public bool JumpPressed;
        public bool SprintHeld;

        public CharacterMotorContext2D(IPhysxBody2D body, IGameWorldManager2D world)
        {
            Body = body;
            World = world;
            DesiredMoveX = 0f;
            DesiredSpeed = 0f;
            Velocity = Vector2.Zero;
            IsGrounded = false;
            JumpPressed = false;
            SprintHeld = false;
        }
    }

    public sealed class KinematicCharacterController2D : IKinematicCharacterController2D
    {
        private IPhysxBody2D? _body;

        // Intents
        private float _moveX;
        private bool _sprintHeld;
        private bool _jumpPressed;
        private float _lookX;

        // Motor state
        private Vector2 _velocity;
        private bool _isGrounded;

        private readonly List<ICharacterAbility2D> _abilities = new();

        // ── Tuning ──────────────────────────────────────────────────────────────
        public float MoveSpeed { get; set; } = 5f;
        public float SprintSpeed { get; set; } = 5f;
        public float Acceleration { get; set; } = 1000f;
        public float Deceleration { get; set; } = 1000f;

        public float Gravity { get; set; } = 25f;
        public float MaxFallSpeed { get; set; } = 50f;

        public float GroundProbeDistance { get; set; } = 0.12f;
        public float SkinWidth { get; set; } = 0.03f;

        // ── Outputs ──────────────────────────────────────────────────────────────
        public Vector2 Position => _body?.Position ?? Vector2.Zero;
        public float RotationZ => _body?.RotationZ ?? 0f;
        public bool IsGrounded => _isGrounded;
        public Vector2 Velocity => _velocity;

        public void AddAbility(ICharacterAbility2D ability)
        {
            if (ability is null)
                throw new ArgumentNullException(nameof(ability));
            _abilities.Add(ability);
        }

        public void ClearAbilities() => _abilities.Clear();

        public void SetBody(IPhysxBody2D body)
        {
            _body = body ?? throw new ArgumentNullException(nameof(body));
        }

        public void MoveIntent(float moveX) => _moveX = moveX;

        public void LookIntent(float lookX, float lookY)
        {
            if (MathF.Abs(lookX) > 1e-6f || MathF.Abs(lookY) > 1e-6f)
                _lookX = MathF.Atan2(lookX, lookY);
        }

        public void SprintIntent(bool sprintHeld) => _sprintHeld = sprintHeld;
        public void JumpIntent(bool jumpPressed) => _jumpPressed = jumpPressed;

        public void Step(float dt, IGameWorldManager2D world)
        {
            if (dt <= 0f || float.IsNaN(dt) || float.IsInfinity(dt))
                return;

            var body = _body;
            if (body is null)
                return;

            // 1) Ground probe
            ProbeGround(body, world, out _isGrounded);

            // 2) Build context
            float desiredSpeed = _sprintHeld ? SprintSpeed : MoveSpeed;
            float desiredMoveX = MathF.Abs(_moveX) > 1e-6f ? Math.Clamp(_moveX, -1f, 1f) : 0f;
            _moveX = 0f;

            var ctx = new CharacterMotorContext2D(body, world)
            {
                DesiredMoveX = desiredMoveX,
                DesiredSpeed = desiredMoveX != 0f ? desiredSpeed : 0f,
                Velocity = _velocity,
                IsGrounded = _isGrounded,
                JumpPressed = _jumpPressed,
                SprintHeld = _sprintHeld,
            };

            // 3) Run abilities
            for (int i = 0; i < _abilities.Count; i++)
                _abilities[i].Step(dt, ref ctx);

            // 4) Gravity
            if (ctx.IsGrounded)
            {
                if (ctx.Velocity.Y < 0f)
                    ctx.Velocity = new Vector2(ctx.Velocity.X, 0f);
            }
            else
            {
                float vy = ctx.Velocity.Y - Gravity * dt;
                vy = MathF.Max(vy, -MathF.Abs(MaxFallSpeed));
                ctx.Velocity = new Vector2(ctx.Velocity.X, vy);
            }

            // 5) Horizontal accel/decel
            float targetVX = ctx.DesiredMoveX * ctx.DesiredSpeed;
            float currentVX = ctx.Velocity.X;
            float accel = ctx.DesiredSpeed > 0f ? MathF.Max(0f, Acceleration) : MathF.Max(0f, Deceleration);

            float newVX = MoveToward(currentVX, targetVX, accel * dt);
            ctx.Velocity = new Vector2(newVX, ctx.Velocity.Y);

            // 6) Apply to body
            body.Position += ctx.Velocity * dt;
            body.LinearVelocity = ctx.Velocity;
            body.AngularVelocityZ = 0f;

            // Face move direction (flip RotationZ based on horizontal dir)
            if (MathF.Abs(desiredMoveX) > 1e-6f)
                body.RotationZ = desiredMoveX > 0f ? 0f : MathF.PI;

            _velocity = ctx.Velocity;
            _jumpPressed = false;

            // Refresh grounded
            ProbeGround(body, world, out _isGrounded);
        }

        private void ProbeGround(IPhysxBody2D body, IGameWorldManager2D world, out bool grounded)
        {
            float probeLen = MathF.Max(0f, GroundProbeDistance + SkinWidth);
            var from = body.Position;
            var to = new Vector2(from.X, from.Y - probeLen);

            if (world.PhysxWorld is IPhysxWorld2D physx2D)
            {
                var hits = physx2D.RayCast(new PhysxRay2D(from, to), maxHits: 4);
                foreach (var h in hits)
                {
                    if (h.Body is not null && !ReferenceEquals(h.Body, body))
                    {
                        grounded = true;
                        return;
                    }
                }
            }

            grounded = false;
        }

        private static float MoveToward(float current, float target, float maxDelta)
        {
            if (maxDelta <= 0f)
                return current;

            float diff = target - current;
            float absDiff = MathF.Abs(diff);
            if (absDiff <= maxDelta)
                return target;

            return current + MathF.Sign(diff) * maxDelta;
        }
    }

    public sealed class SimpleJumpAbility2D : ICharacterAbility2D
    {
        public float JumpSpeed { get; set; } = 7.5f;

        private bool _wasPressed;

        public void Step(float dt, ref CharacterMotorContext2D ctx)
        {
            bool pressedThisFrame = ctx.JumpPressed && !_wasPressed;
            _wasPressed = ctx.JumpPressed;

            if (!pressedThisFrame)
                return;

            if (ctx.IsGrounded)
            {
                ctx.Velocity = new Vector2(ctx.Velocity.X, JumpSpeed);
                ctx.IsGrounded = false;
            }
        }
    }
}
