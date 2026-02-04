using System.Numerics;

using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;

namespace Altruist.Gaming.ThreeD;

public interface IKinematicCharacterController3D
{
    void SetBody(IPhysxBody3D body);

    // Intents (called by netcode)
    void MoveIntent(float moveX, float moveZ);
    void LookIntent(float lookX, float lookY, float lookZ);
    void SprintIntent(bool sprintHeld);
    void JumpIntent(bool jumpPressed);

    // Simulation tick (called by prefab/world Step)
    void Step(float dt, IGameWorldManager3D world);

    // State outputs (for replication)
    Vector3 Position { get; }
    Quaternion Rotation { get; }
    bool IsGrounded { get; }
    Vector3 Velocity { get; } // controller velocity (including gravity)
}

// Single hook (no pre/post)
public interface ICharacterAbility3D
{
    void Step(float dt, ref CharacterMotorContext ctx);
}

public struct CharacterMotorContext
{
    public IPhysxBody3D Body;
    public IGameWorldManager3D World;

    public Vector3 DesiredMoveWorld; // normalized on XZ (or zero)
    public float DesiredSpeed;       // m/s
    public Vector3 Velocity;         // motor-controlled velocity (mutable)

    public bool IsGrounded;
    public Vector3 GroundNormal;

    public bool JumpPressed;
    public bool SprintHeld;

    public CharacterMotorContext(IPhysxBody3D body, IGameWorldManager3D world)
    {
        Body = body;
        World = world;

        DesiredMoveWorld = Vector3.Zero;
        DesiredSpeed = 0f;
        Velocity = Vector3.Zero;

        IsGrounded = false;
        GroundNormal = Vector3.UnitY;

        JumpPressed = false;
        SprintHeld = false;
    }
}

public sealed class KinematicCharacterController3D : IKinematicCharacterController3D
{
    private IPhysxBody3D? _body;

    // intents
    private Vector3 _moveLocal;
    private bool _sprintHeld;
    private bool _jumpPressed;

    // camera
    private float _cameraYaw;
    private float _cameraPitch;

    // motor state
    private float _yaw;
    private Vector3 _velocity;
    private bool _isGrounded;
    private Vector3 _groundNormal = Vector3.UnitY;

    // abilities (optional)
    private readonly List<ICharacterAbility3D> _abilities = new();

    // ─────────────────────────────────────────────
    // Tuning knobs (defaults chosen to be "optional":
    // - SprintSpeed == MoveSpeed
    // - Acceleration == Deceleration
    // - High accel/decel so default feels immediate/no drift
    // ─────────────────────────────────────────────

    // Max speed (walk/sprint)
    public float MoveSpeed { get; set; } = 5f;
    public float SprintSpeed { get; set; } = 5f; // default == MoveSpeed (optional knob)

    // Acceleration / deceleration (m/s^2)
    public float Acceleration { get; set; } = 1000f;
    public float Deceleration { get; set; } = 1000f; // default == Acceleration (optional knob)

    // Air control
    public float AirAccelerationMultiplier { get; set; } = 1f;
    public float AirMaxSpeedMultiplier { get; set; } = 1f;

    // Vertical
    public float Gravity { get; set; } = 25f;      // m/s^2 downward
    public float MaxFallSpeed { get; set; } = 50f; // clamp

    // Grounding/slope
    public float GroundProbeDistance { get; set; } = 0.12f;
    public float SkinWidth { get; set; } = 0.03f;
    public float MaxSlopeAngleDeg { get; set; } = 60f;
    public float GroundSnapSpeed { get; set; } = 2f; // tiny downward velocity when grounded

    public PhysxLayer GroundMask { get; set; } = PhysxLayer.World;

    // Rotation
    public bool FaceMoveDirection { get; set; } = true;
    public float RotationSpeedDegPerSec { get; set; } = 0f; // 0 => snap

    // Character shape for grounding probe (set from capsule profile)
    public float Radius { get; set; } = 0.28f;
    public float Height { get; set; } = 1.8f;

    // Optional external facing (for combat stance / lock-on)
    // If enabled, controller will face ExternalFacingYaw (radians) instead of move direction/camera.
    public bool UseExternalFacingYaw { get; set; } = false;
    public float ExternalFacingYaw { get; set; } = 0f;

    // Outputs
    public Vector3 Position => _body?.Position ?? Vector3.Zero;
    public Quaternion Rotation => _body?.Rotation ?? Quaternion.Identity;
    public bool IsGrounded => _isGrounded;
    public Vector3 Velocity => _velocity;

    public void AddAbility(ICharacterAbility3D ability)
    {
        if (ability == null)
            throw new ArgumentNullException(nameof(ability));
        _abilities.Add(ability);
    }

    public void ClearAbilities() => _abilities.Clear();

    public void SetBody(IPhysxBody3D body)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _yaw = ExtractYaw(body.Rotation);
    }

    public void MoveIntent(float moveX, float moveZ)
        => _moveLocal = new Vector3(moveX, 0f, moveZ);

    public void SprintIntent(bool sprintHeld) => _sprintHeld = sprintHeld;

    public void JumpIntent(bool jumpPressed) => _jumpPressed = jumpPressed;

    public void LookIntent(float lookX, float lookY, float lookZ)
    {
        var fwd = new Vector3(lookX, lookY, lookZ);
        if (fwd.LengthSquared() < 1e-10f)
            return;

        fwd = Vector3.Normalize(fwd);
        _cameraYaw = MathF.Atan2(fwd.X, fwd.Z);

        float pitch = MathF.Asin(Math.Clamp(fwd.Y, -1f, 1f));
        _cameraPitch = Math.Clamp(pitch, -1.55f, 1.55f);
    }

    public void Step(float dt, IGameWorldManager3D world)
    {
        if (dt <= 0f || float.IsNaN(dt) || float.IsInfinity(dt))
            return;

        var body = _body;
        if (body == null)
            return;

        // 1) Ground probe (start-of-step)
        ProbeGround(body, world, out _isGrounded, out _groundNormal);

        // 2) Desired move in world space (camera-relative)
        var moveLocal = _moveLocal;
        _moveLocal = Vector3.Zero;

        var forward = CalculateForward(_cameraYaw);
        var right = CalculateRight(_cameraYaw);

        var moveWorld = (right * moveLocal.X) + (forward * moveLocal.Z);
        moveWorld.Y = 0f;

        float mag = moveWorld.Length();
        if (mag > 1e-6f)
            moveWorld /= mag;
        else
            moveWorld = Vector3.Zero;

        float baseSpeed = _sprintHeld ? SprintSpeed : MoveSpeed;
        baseSpeed = MathF.Max(0f, baseSpeed);

        float desiredSpeed = (moveWorld.LengthSquared() > 1e-10f) ? baseSpeed : 0f;

        // 3) Yaw-only rotation
        float targetYaw;
        if (UseExternalFacingYaw)
        {
            targetYaw = ExternalFacingYaw;
        }
        else if (FaceMoveDirection && desiredSpeed > 0f)
        {
            targetYaw = MathF.Atan2(moveWorld.X, moveWorld.Z);
        }
        else
        {
            targetYaw = _cameraYaw;
        }

        _yaw = (RotationSpeedDegPerSec > 0f)
            ? MoveTowardAngleRad(_yaw, targetYaw, (RotationSpeedDegPerSec * (MathF.PI / 180f)) * dt)
            : WrapAngleRad(targetYaw);

        body.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _yaw);

        // 4) Context for abilities (single hook)
        var ctx = new CharacterMotorContext(body, world)
        {
            DesiredMoveWorld = moveWorld,
            DesiredSpeed = desiredSpeed,
            Velocity = _velocity,
            IsGrounded = _isGrounded,
            GroundNormal = _groundNormal,
            JumpPressed = _jumpPressed,
            SprintHeld = _sprintHeld
        };

        for (int i = 0; i < _abilities.Count; i++)
            _abilities[i].Step(dt, ref ctx);

        // 5) Gravity + ground snap
        if (ctx.IsGrounded)
        {
            if (ctx.Velocity.Y < 0f)
                ctx.Velocity = new Vector3(ctx.Velocity.X, -MathF.Abs(GroundSnapSpeed), ctx.Velocity.Z);
        }
        else
        {
            float vy = ctx.Velocity.Y - Gravity * dt;
            vy = MathF.Max(vy, -MathF.Abs(MaxFallSpeed));
            ctx.Velocity = new Vector3(ctx.Velocity.X, vy, ctx.Velocity.Z);
        }

        // 6) Horizontal accel/decel (friction-ish stop)
        var v = ctx.Velocity;
        var vH = new Vector3(v.X, 0f, v.Z);

        float accel = MathF.Max(0f, Acceleration);
        float decel = MathF.Max(0f, Deceleration);

        if (!ctx.IsGrounded)
        {
            accel *= MathF.Max(0f, AirAccelerationMultiplier);
            decel *= MathF.Max(0f, AirAccelerationMultiplier);
        }

        Vector3 targetVH = Vector3.Zero;
        if (ctx.DesiredSpeed > 0f && ctx.DesiredMoveWorld.LengthSquared() > 1e-10f)
        {
            float maxSpeed = ctx.IsGrounded
                ? ctx.DesiredSpeed
                : ctx.DesiredSpeed * MathF.Max(0f, AirMaxSpeedMultiplier);

            targetVH = ctx.DesiredMoveWorld * maxSpeed;
        }

        Vector3 newVH;
        if (targetVH.LengthSquared() < 1e-10f)
        {
            // no input => decelerate to 0
            newVH = MoveToward(vH, Vector3.Zero, decel * dt);
        }
        else
        {
            // input => accelerate toward target
            newVH = MoveToward(vH, targetVH, accel * dt);
        }

        ctx.Velocity = new Vector3(newVH.X, ctx.Velocity.Y, newVH.Z);

        // 7) Apply kinematic motion (baseline: no sweep yet)
        body.Position += ctx.Velocity * dt;

        // For replication / gameplay reads
        body.LinearVelocity = ctx.Velocity;
        body.AngularVelocity = Vector3.Zero;

        // 8) Store state
        _velocity = ctx.Velocity;

        // Optional: refresh grounded after move
        ProbeGround(body, world, out _isGrounded, out _groundNormal);
    }

    private void ProbeGround(IPhysxBody3D body, IGameWorldManager3D world, out bool grounded, out Vector3 normal)
    {
        // Ray origin: just above bottom hemisphere (inside skin)
        float halfHeight = Height * 0.5f;
        float bottomOffset = MathF.Max(0f, halfHeight - Radius);

        var origin = body.Position + new Vector3(0f, -(bottomOffset - SkinWidth), 0f);
        var target = origin + new Vector3(0f, -(GroundProbeDistance + SkinWidth), 0f);

        var hits = world.PhysxWorld.Engine.RayCast(
            new PhysxRay3D(origin, target),
            maxHits: 4,
            layerMask: (uint)GroundMask
        );

        bool gotHit = false;
        PhysxRaycastHit3D best = default;

        foreach (var h in hits)
        {
            if (h.Body == null)
                continue;

            if (ReferenceEquals(h.Body, body))
                continue;

            best = h;
            gotHit = true;
            break;
        }

        if (!gotHit)
        {
            grounded = false;
            normal = Vector3.UnitY;
            return;
        }

        // Slope test
        float maxSlopeRad = MathF.Abs(MaxSlopeAngleDeg) * (MathF.PI / 180f);
        float minNy = MathF.Cos(maxSlopeRad);

        grounded = best.Normal.Y >= minNy;
        normal = best.Normal;
    }

    private static Vector3 CalculateForward(float yaw)
        => new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));

    private static Vector3 CalculateRight(float yaw)
        => new Vector3(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));

    private static Vector3 MoveToward(Vector3 current, Vector3 target, float maxDelta)
    {
        if (maxDelta <= 0f)
            return current;

        var delta = target - current;
        float len = delta.Length();
        if (len <= maxDelta || len < 1e-10f)
            return target;

        return current + (delta / len) * maxDelta;
    }

    private static float WrapAngleRad(float r)
    {
        while (r > MathF.PI)
            r -= MathF.Tau;
        while (r < -MathF.PI)
            r += MathF.Tau;
        return r;
    }

    private static float DeltaAngleRad(float current, float target)
        => WrapAngleRad(target - current);

    private static float MoveTowardAngleRad(float current, float target, float maxDeltaRad)
    {
        float delta = DeltaAngleRad(current, target);
        if (MathF.Abs(delta) <= maxDeltaRad)
            return WrapAngleRad(target);

        return WrapAngleRad(current + MathF.Sign(delta) * maxDeltaRad);
    }

    private static float ExtractYaw(Quaternion q)
    {
        float siny = 2f * (q.W * q.Y + q.X * q.Z);
        float cosy = 1f - 2f * (q.Y * q.Y + q.X * q.X);
        return MathF.Atan2(siny, cosy);
    }
}

public sealed class SimpleJumpAbility3D : ICharacterAbility3D
{
    public float JumpSpeed { get; set; } = 7.5f;

    private bool _wasPressed;

    public void Step(float dt, ref CharacterMotorContext ctx)
    {
        bool pressedThisFrame = ctx.JumpPressed && !_wasPressed;
        _wasPressed = ctx.JumpPressed;

        if (!pressedThisFrame)
            return;

        if (ctx.IsGrounded)
        {
            ctx.Velocity = new Vector3(ctx.Velocity.X, JumpSpeed, ctx.Velocity.Z);
            ctx.IsGrounded = false;
        }
    }
}
