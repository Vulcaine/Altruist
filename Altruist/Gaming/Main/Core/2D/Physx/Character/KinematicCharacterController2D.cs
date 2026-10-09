/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Physx.TwoD;

namespace Altruist.Gaming.TwoD
{
    /// <summary>Side-view character motor: turns per-tick input intents (move, sprint, jump) into
    /// body motion with gravity, a ground probe and pluggable <see cref="ICharacterAbility2D"/>s.
    /// <para>Use it for walking / jumping characters driven by player or bot input. For vehicles,
    /// balls and other bodies driven by the physics solver use <see cref="GameplayVerbs2D"/> on the
    /// body instead; for 3D characters use <see cref="Altruist.Gaming.ThreeD.IKinematicCharacterController3D"/>.</para>
    /// <para>Usage: set intents (typically from the input handler), then call <see cref="Step"/> once
    /// per world tick on the tick thread. Intents are consumed by the step: the move and jump intents
    /// are reset after each <see cref="Step"/>, so send them every tick while held.</para></summary>
    public interface IKinematicCharacterController2D
    {
        /// <summary>Binds the body the controller moves. Must be called before <see cref="Step"/>
        /// (which does nothing without a body).</summary>
        /// <exception cref="ArgumentNullException"><paramref name="body"/> is null.</exception>
        void SetBody(IPhysxBody2D body);

        /// <summary>Horizontal movement intent (-1 to 1 normalized).</summary>
        void MoveIntent(float moveX);

        /// <summary>Look direction intent (for facing).</summary>
        void LookIntent(float lookX, float lookY);

        /// <summary>Sprint intent: while true the controller targets the sprint speed instead of the walk speed.</summary>
        void SprintIntent(bool sprintHeld);
        /// <summary>Jump intent for the next <see cref="Step"/> (reset after it). Whether and how a
        /// jump happens is up to the registered abilities (e.g. <see cref="SimpleJumpAbility2D"/>).</summary>
        void JumpIntent(bool jumpPressed);

        /// <summary>Advances the controller by <paramref name="dt"/> seconds: probes the ground, runs
        /// abilities, applies gravity and horizontal acceleration, then writes position and velocity
        /// to the body. Call once per world tick on the tick thread.</summary>
        /// <param name="dt">Step length in seconds; non-positive, NaN or infinite values skip the step.</param>
        /// <param name="world">The world whose physics is ray-cast for the ground probe.</param>
        void Step(float dt, IGameWorldManager2D world);

        // State outputs
        /// <summary>The body's current position (zero when no body is bound).</summary>
        Vector2 Position { get; }
        /// <summary>The body's rotation in radians (zero when no body is bound).</summary>
        float RotationZ { get; }
        /// <summary>Whether the last ground probe found ground under the body.</summary>
        bool IsGrounded { get; }
        /// <summary>The controller's velocity after the last step (world units per second, +Y up).</summary>
        Vector2 Velocity { get; }
    }

    /// <summary>A pluggable behaviour run inside <see cref="KinematicCharacterController2D.Step"/>
    /// before gravity and horizontal movement are applied (jump, dash, wall-slide...). Abilities run
    /// in registration order and edit the shared <see cref="CharacterMotorContext2D"/>.</summary>
    public interface ICharacterAbility2D
    {
        /// <summary>Runs the ability for one step by editing <paramref name="ctx"/> (e.g. set
        /// <c>ctx.Velocity</c>, clear <c>ctx.IsGrounded</c>).</summary>
        /// <param name="dt">Step length in seconds.</param>
        /// <param name="ctx">The motor state for this step; changes are seen by later abilities and the motor.</param>
        void Step(float dt, ref CharacterMotorContext2D ctx);
    }

    /// <summary>Mutable per-step motor state passed by reference to each <see cref="ICharacterAbility2D"/>.</summary>
    public struct CharacterMotorContext2D
    {
        /// <summary>The controlled body.</summary>
        public IPhysxBody2D Body;
        /// <summary>The world being stepped.</summary>
        public IGameWorldManager2D World;

        /// <summary>Horizontal move direction, clamped to -1..1 (0 when there is no move intent).</summary>
        public float DesiredMoveX;   // -1..1 normalized horizontal direction
        /// <summary>Target horizontal speed in world units per second (walk or sprint speed; 0 without move intent).</summary>
        public float DesiredSpeed;   // m/s

        /// <summary>Controller velocity (+Y up); abilities may overwrite it.</summary>
        public Vector2 Velocity;
        /// <summary>Whether the body is on the ground this step; an ability that leaves the ground should clear it.</summary>
        public bool IsGrounded;
        /// <summary>The jump intent for this step.</summary>
        public bool JumpPressed;
        /// <summary>The sprint intent for this step.</summary>
        public bool SprintHeld;

        /// <summary>Creates a context for <paramref name="body"/> in <paramref name="world"/> with every other field zeroed.</summary>
        /// <param name="body">The controlled body.</param>
        /// <param name="world">The world being stepped.</param>
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

    /// <summary>Default <see cref="IKinematicCharacterController2D"/>: a simple side-view platformer
    /// motor. Each <see cref="Step"/>: ray-casts down from the body's position by
    /// <see cref="GroundProbeDistance"/> + <see cref="SkinWidth"/> (any body other than its own counts as
    /// ground), runs the abilities, applies <see cref="Gravity"/> clamped at <see cref="MaxFallSpeed"/>
    /// in the air (and zeroes downward speed on the ground), moves horizontal speed toward the target
    /// with <see cref="Acceleration"/> / <see cref="Deceleration"/>, then sets
    /// <c>body.Position += v * dt</c>, <c>LinearVelocity = v</c>, <c>AngularVelocityZ = 0</c> and faces the
    /// body right (rotation 0) or left (rotation π) by the move direction.
    /// <para>Limits: the motor teleports the body (no swept collision against walls), and the probe
    /// starts at the body's center, so <see cref="GroundProbeDistance"/> must cover the distance from
    /// center to feet. Not thread-safe; create one per character (not a DI service).</para>
    /// <example><code>
    /// var kcc = new KinematicCharacterController2D { MoveSpeed = 6f, SprintSpeed = 9f, GroundProbeDistance = 1.05f };
    /// kcc.SetBody(body);
    /// kcc.AddAbility(new SimpleJumpAbility2D { JumpSpeed = 8f });
    /// // each tick:
    /// kcc.MoveIntent(input.X); kcc.JumpIntent(input.Jump);
    /// kcc.Step(dt, world);
    /// </code></example></summary>
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
        /// <summary>Walk speed in world units per second (default 5).</summary>
        public float MoveSpeed { get; set; } = 5f;
        /// <summary>Speed while the sprint intent is held, world units per second (default 5, same as walk).</summary>
        public float SprintSpeed { get; set; } = 5f;
        /// <summary>Horizontal acceleration toward the target speed while moving, units/s² (default 1000, effectively instant).</summary>
        public float Acceleration { get; set; } = 1000f;
        /// <summary>Horizontal deceleration toward zero without move intent, units/s² (default 1000).</summary>
        public float Deceleration { get; set; } = 1000f;

        /// <summary>Downward acceleration in the air, units/s² (default 25).</summary>
        public float Gravity { get; set; } = 25f;
        /// <summary>Maximum fall speed, units/s (default 50; the sign is ignored).</summary>
        public float MaxFallSpeed { get; set; } = 50f;

        /// <summary>Length of the downward ground ray from the body's position (center), world units (default 0.12).</summary>
        public float GroundProbeDistance { get; set; } = 0.12f;
        /// <summary>Extra length added to the ground ray, world units (default 0.03).</summary>
        public float SkinWidth { get; set; } = 0.03f;

        // ── Outputs ──────────────────────────────────────────────────────────────
        /// <inheritdoc/>
        public Vector2 Position => _body?.Position ?? Vector2.Zero;
        /// <inheritdoc/>
        public float RotationZ => _body?.RotationZ ?? 0f;
        /// <inheritdoc/>
        public bool IsGrounded => _isGrounded;
        /// <inheritdoc/>
        public Vector2 Velocity => _velocity;

        /// <summary>Appends an ability; abilities run in the order added, each step.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="ability"/> is null.</exception>
        public void AddAbility(ICharacterAbility2D ability)
        {
            if (ability is null)
                throw new ArgumentNullException(nameof(ability));
            _abilities.Add(ability);
        }

        /// <summary>Removes all abilities.</summary>
        public void ClearAbilities() => _abilities.Clear();

        /// <inheritdoc/>
        public void SetBody(IPhysxBody2D body)
        {
            _body = body ?? throw new ArgumentNullException(nameof(body));
        }

        /// <inheritdoc/>
        public void MoveIntent(float moveX) => _moveX = moveX;

        /// <summary>Records a look direction. Currently stored but not used by the motor (facing
        /// follows the move direction).</summary>
        public void LookIntent(float lookX, float lookY)
        {
            if (MathF.Abs(lookX) > 1e-6f || MathF.Abs(lookY) > 1e-6f)
                _lookX = MathF.Atan2(lookX, lookY);
        }

        /// <inheritdoc/>
        public void SprintIntent(bool sprintHeld) => _sprintHeld = sprintHeld;
        /// <inheritdoc/>
        public void JumpIntent(bool jumpPressed) => _jumpPressed = jumpPressed;

        /// <inheritdoc/>
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

    /// <summary>Minimal jump ability: on the rising edge of the jump intent, while grounded, sets the
    /// vertical velocity to <see cref="JumpSpeed"/> and clears <c>IsGrounded</c>. No coyote time, buffering
    /// or variable height; write your own <see cref="ICharacterAbility2D"/> for those.</summary>
    public sealed class SimpleJumpAbility2D : ICharacterAbility2D
    {
        /// <summary>Upward speed set on jump, world units per second (default 7.5).</summary>
        public float JumpSpeed { get; set; } = 7.5f;

        private bool _wasPressed;

        /// <inheritdoc/>
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
