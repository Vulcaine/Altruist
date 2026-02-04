using System.Numerics;

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
    void Step(float dt, IPhysxWorldEngine3D world);

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
    public IPhysxWorldEngine3D World;

    public Vector3 DesiredMoveWorld;
    public float DesiredSpeed;
    public Vector3 Velocity;

    public bool IsGrounded;
    public Vector3 GroundNormal;

    public bool JumpPressed;
    public bool SprintHeld;

    public CharacterMotorContext(IPhysxBody3D body, IPhysxWorldEngine3D world)
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

    // Tuning knobs (framework defaults)
    public float MoveSpeed { get; set; } = 5f;
    public float SprintSpeed { get; set; } = 8f;

    // Character shape for grounding probe (you should set these from your capsule profile)
    public float Radius { get; set; } = 0.28f;
    public float Height { get; set; } = 1.8f;

    // Grounding
    public float SkinWidth { get; set; } = 0.03f;          // small offset to avoid snagging
    public float GroundProbeDistance { get; set; } = 0.12f; // extra ray length below capsule
    public float MaxSlopeAngleDeg { get; set; } = 60f;

    // Gravity
    public float Gravity { get; set; } = 25f;      // m/s^2 downward
    public float MaxFallSpeed { get; set; } = 50f; // clamp

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

    public void Step(float dt, IPhysxWorldEngine3D world)
    {
        if (dt <= 0f)
            return;

        var body = _body;
        if (body == null)
            return;

        // 1) Ground probe (start-of-step)
        ProbeGround(body, world, out _isGrounded, out _groundNormal);

        // 2) Build desired move in world space (camera-relative)
        var moveLocal = _moveLocal;
        _moveLocal = Vector3.Zero;

        var forward = CalculateForward(_cameraYaw);
        var right = CalculateRight(_cameraYaw);

        var moveWorld = (right * moveLocal.X) + (forward * moveLocal.Z);
        moveWorld.Y = 0f;

        float mag = moveWorld.Length();
        if (mag > 1e-6f)
            moveWorld /= mag;

        float speed = _sprintHeld ? SprintSpeed : MoveSpeed;
        speed = MathF.Max(0f, speed);

        // 3) Rotate yaw-only:
        // if moving, face move direction; else face camera yaw
        if (mag > 1e-6f)
            _yaw = MathF.Atan2(moveWorld.X, moveWorld.Z);
        else
            _yaw = _cameraYaw;

        body.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _yaw);

        // 4) Context for abilities (single hook)
        var ctx = new CharacterMotorContext(body, world)
        {
            DesiredMoveWorld = moveWorld,
            DesiredSpeed = (mag > 1e-6f) ? speed : 0f,
            Velocity = _velocity,
            IsGrounded = _isGrounded,
            GroundNormal = _groundNormal,
            JumpPressed = _jumpPressed,
            SprintHeld = _sprintHeld
        };

        for (int i = 0; i < _abilities.Count; i++)
            _abilities[i].Step(dt, ref ctx);

        // 5) Gravity (controller-owned)
        if (ctx.IsGrounded)
        {
            // keep tiny downward to stick (or set to 0 if you prefer)
            if (ctx.Velocity.Y < 0f)
                ctx.Velocity = new Vector3(ctx.Velocity.X, -2f, ctx.Velocity.Z);
        }
        else
        {
            float vy = ctx.Velocity.Y - Gravity * dt;
            vy = MathF.Max(vy, -MaxFallSpeed);
            ctx.Velocity = new Vector3(ctx.Velocity.X, vy, ctx.Velocity.Z);
        }

        // 6) Horizontal velocity from intent
        var desiredH = (ctx.DesiredSpeed <= 0f)
            ? Vector3.Zero
            : ctx.DesiredMoveWorld * ctx.DesiredSpeed;

        ctx.Velocity = new Vector3(desiredH.X, ctx.Velocity.Y, desiredH.Z);

        // 7) Apply kinematic motion (baseline: no sweep yet)
        body.Position += ctx.Velocity * dt;

        // Helpful for replication + interaction (depending on engine)
        body.LinearVelocity = ctx.Velocity;
        body.AngularVelocity = Vector3.Zero;

        // 8) Store state
        _velocity = ctx.Velocity;

        // Optional: refresh grounded after move
        ProbeGround(body, world, out _isGrounded, out _groundNormal);
    }

    private void ProbeGround(IPhysxBody3D body, IPhysxWorldEngine3D world, out bool grounded, out Vector3 normal)
    {
        // Ray origin: just above bottom hemisphere (inside skin)
        float halfHeight = Height * 0.5f;
        float bottomOffset = MathF.Max(0f, halfHeight - Radius);

        var origin = body.Position + new Vector3(0f, -(bottomOffset - SkinWidth), 0f);
        var target = origin + new Vector3(0f, -(GroundProbeDistance + SkinWidth), 0f);

        var hits = world.RayCast(new PhysxRay3D(origin, target), maxHits: 1);
        var hit = hits.FirstOrDefault();

        if (hit.Body == null)
        {
            grounded = false;
            normal = Vector3.UnitY;
            return;
        }

        // Slope test
        float maxSlopeRad = MaxSlopeAngleDeg * (MathF.PI / 180f);
        float minNy = MathF.Cos(maxSlopeRad);

        grounded = hit.Normal.Y >= minNy;
        normal = hit.Normal;
    }

    private static Vector3 CalculateForward(float yaw)
        => new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));

    private static Vector3 CalculateRight(float yaw)
        => new Vector3(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));

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
            var v = ctx.Velocity;
            v.Y = JumpSpeed;
            ctx.Velocity = v;

            ctx.IsGrounded = false;
        }
    }
}
