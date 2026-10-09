using System.Numerics;

using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;

namespace Altruist.Gaming.ThreeD;

/// <summary>
/// Server-authoritative character mover for a 3D body: takes latched movement input, applies gravity, grounding,
/// acceleration and collide-and-slide, and writes the result to the body each tick.
/// </summary>
/// <remarks>
/// The framework implementation is <see cref="KinematicCharacterController3D"/>. Use a character controller for
/// player/NPC avatars that must walk, slide along walls and stick to slopes; for simple steering of bodies without
/// collision response use the body steering/navigation extension helpers, and for fully simulated objects (balls, crates)
/// let the physics solver drive a dynamic body instead. All members are expected to be called from the world tick thread.
/// </remarks>
public interface IKinematicCharacterController3D
{
    /// <summary>Attaches the body the controller moves (typically a kinematic capsule) and adopts its current yaw.</summary>
    /// <param name="body">Body to drive; its <see cref="IPhysxBody3D.Position"/> is the capsule centre.</param>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is null.</exception>
    void SetBody(IPhysxBody3D body);

    /// <summary>
    /// Replaces the latched movement input (null resets to <see cref="CharacterRealtimeWasdInput3D.Neutral"/>). The input keeps
    /// applying every <see cref="Step"/> until replaced; click-to-move input resets itself on arrival.
    /// </summary>
    /// <param name="input">New input, interpreted by the active <see cref="IMovementProfile3D"/>.</param>
    void SetMovementInput(ICharacterMovementInput3D input);

    /// <summary>
    /// Queues a one-shot displacement in the character's local frame (X right, Y up, Z forward relative to the current yaw),
    /// e.g. root motion from an animation or a dash. Accumulates until the next <see cref="Step"/>, which applies it (with
    /// sweeps when enabled) before regular movement and then clears it. Does not change velocity.
    /// </summary>
    /// <param name="localDelta">Displacement in world units; near-zero values are ignored.</param>
    void AddLocalMotionDelta(Vector3 localDelta);

    // Simulation tick (called by prefab/world Step)
    /// <summary>Advances the controller by one fixed tick and writes position, rotation and velocity to the body. No-op without a body or for a non-positive/non-finite <paramref name="dt"/>.</summary>
    /// <param name="dt">Tick length in seconds.</param>
    /// <param name="world">World whose physics engine (or the controller's query provider) is used for sweeps and ground probes.</param>
    void Step(float dt, IGameWorldManager3D world);

    // State outputs (for replication)
    /// <summary>Body position (capsule centre), or zero when no body is attached.</summary>
    Vector3 Position { get; }
    /// <summary>Body rotation (yaw-only), or identity when no body is attached.</summary>
    Quaternion Rotation { get; }
    /// <summary>Current facing yaw in radians, wrapped to [-PI, PI]; 0 faces +Z.</summary>
    float Yaw { get; }
    /// <summary>Whether the last ground probe found walkable ground (slope within the max slope angle).</summary>
    bool IsGrounded { get; }
    /// <summary>Controller velocity in world units per second after the last step, including gravity.</summary>
    Vector3 Velocity { get; } // controller velocity (including gravity)
    /// <summary>The movement frame evaluated by the movement profile on the last step.</summary>
    CharacterMovementFrame3D LastMovementFrame { get; }
    /// <summary>True while a click-to-move target is pending or the realtime input has a non-trivial move axis (|axis| &gt;= 0.01) or jump held.</summary>
    bool HasActiveMovementInput { get; }
}

// Single hook (no pre/post)
/// <summary>
/// Pluggable behaviour (jump, dash, glide, ...) that can modify the motor state once per
/// <see cref="KinematicCharacterController3D.Step"/>, after facing is resolved and before gravity, acceleration and the move.
/// </summary>
/// <remarks>
/// Register with <see cref="KinematicCharacterController3D.AddAbility"/>; abilities run in registration order. To leave the
/// ground (e.g. jump) set <see cref="CharacterMotorContext.IsGrounded"/> to false together with a positive vertical velocity,
/// otherwise the grounded ground-snap overwrites <c>Velocity.Y</c>. For one-off displacements prefer
/// <see cref="KinematicCharacterController3D.AddLocalMotionDelta"/>.
/// </remarks>
/// <example><code>
/// controller.AddAbility(new SimpleJumpAbility3D { JumpSpeed = 8f });
/// </code></example>
public interface ICharacterAbility3D
{
    /// <summary>Called once per controller step; mutate <paramref name="ctx"/> to change the motion.</summary>
    /// <param name="dt">Tick length in seconds.</param>
    /// <param name="ctx">Mutable motor state for this tick.</param>
    void Step(float dt, ref CharacterMotorContext ctx);
}

/// <summary>Mutable per-tick motor state handed to every <see cref="ICharacterAbility3D"/> by reference.</summary>
public struct CharacterMotorContext
{
    /// <summary>Body being moved.</summary>
    public IPhysxBody3D Body;
    /// <summary>World the controller is stepping in.</summary>
    public IGameWorldManager3D World;

    /// <summary>Desired horizontal move direction in world space, normalised on XZ, or zero when idle.</summary>
    public Vector3 DesiredMoveWorld; // normalized on XZ (or zero)
    /// <summary>Target horizontal speed in world units per second (0 when idle).</summary>
    public float DesiredSpeed;       // m/s
    /// <summary>Controller velocity in world units per second; abilities may overwrite it.</summary>
    public Vector3 Velocity;         // motor-controlled velocity (mutable)

    /// <summary>Grounded state from the start-of-tick probe. Set false to apply gravity this tick (required for jumps).</summary>
    public bool IsGrounded;
    /// <summary>Ground surface normal (+Y when airborne or unknown).</summary>
    public Vector3 GroundNormal;

    /// <summary>Jump button currently held (level, not edge; abilities detect the edge themselves).</summary>
    public bool JumpPressed;
    /// <summary>Sprint currently held.</summary>
    public bool SprintHeld;

    /// <summary>Creates a context with zero motion, not grounded and an up ground normal.</summary>
    /// <param name="body">Body being moved.</param>
    /// <param name="world">World being stepped.</param>
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

/// <summary>
/// Default <see cref="IKinematicCharacterController3D"/>: capsule character mover with camera-relative input via an
/// <see cref="IMovementProfile3D"/>, yaw-only rotation, gravity, ground probing with a max slope, acceleration/deceleration,
/// capsule collide-and-slide, depenetration, terrain snapping and pluggable <see cref="ICharacterAbility3D"/>s.
/// </summary>
/// <remarks>
/// <para>
/// Not a DI service: create one per character, call <see cref="SetBody"/> (a kinematic capsule, e.g. from
/// <see cref="HumanoidCapsuleBodyProfile"/>, with <see cref="Radius"/>/<see cref="Height"/> matching it), optionally
/// <see cref="SetQueryProvider"/>, then call <see cref="Step"/> once per world tick on the tick thread. Not thread-safe.
/// Positions are capsule centres; +Y is up; yaw is radians with 0 facing +Z.
/// </para>
/// <para>
/// Spatial queries go to the <see cref="ISpatialQueryProvider"/> if one was set, otherwise directly to the world's physics
/// engine (<see cref="IGameWorldManager3D.PhysxWorld"/>). Terrain snapping on slopes only runs with a query provider. Note
/// that hits coming from a query provider carry no body, and the sweep/depenetration passes ignore body-less hits, so with
/// a provider set walls and obstacles do not block movement; only ground probing and terrain snapping use those hits.
/// </para>
/// <para>Step order: profile evaluation, depenetration, ground probe, authored local motion, facing, abilities, gravity,
/// horizontal accel/decel, sweep-and-slide move, depenetration, body velocity write-back, final ground probe.</para>
/// </remarks>
/// <example><code>
/// var kcc = new KinematicCharacterController3D { MovementProfile = TpsMovementProfile3D.Default, Radius = 0.3f, Height = 1.8f };
/// kcc.SetBody(body);
/// kcc.AddAbility(new SimpleJumpAbility3D());
/// // per input packet:
/// kcc.SetMovementInput(new CharacterRealtimeWasdInput3D(x, z, look, 0f, 0f, false, sprint, jump));
/// // per tick:
/// kcc.Step(dt, world);
/// </code></example>
public sealed class KinematicCharacterController3D : IKinematicCharacterController3D
{
    private IPhysxBody3D? _body;
    private ISpatialQueryProvider? _queries;

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
    private IMovementProfile3D? _configuredMovementProfile;
    private ICharacterMovementInput3D _movementInput = CharacterRealtimeWasdInput3D.Neutral;
    private Vector3 _authoredLocalMotionDelta;

    // ─────────────────────────────────────────────
    // Tuning knobs (defaults chosen to be "optional"):
    // - Acceleration == Deceleration
    // - High accel/decel so default feels immediate/no drift.
    // Per-character movement/facing values come from MovementStats + MovementProfile.
    // ─────────────────────────────────────────────

    // Acceleration / deceleration (m/s^2)
    /// <summary>Horizontal acceleration toward the target velocity, world units/s². Default 1000 (effectively instant).</summary>
    public float Acceleration { get; set; } = 1000f;
    /// <summary>Horizontal deceleration toward zero when there is no move input, world units/s². Default 1000.</summary>
    public float Deceleration { get; set; } = 1000f; // default == Acceleration (optional knob)

    // Air control
    /// <summary>Multiplier on both <see cref="Acceleration"/> and <see cref="Deceleration"/> while airborne (air control). Default 1.</summary>
    public float AirAccelerationMultiplier { get; set; } = 1f;
    /// <summary>Multiplier on the target horizontal speed while airborne. Default 1.</summary>
    public float AirMaxSpeedMultiplier { get; set; } = 1f;

    // Vertical
    /// <summary>Downward acceleration while airborne, world units/s² (positive number). Default 25; the movement profile's <see cref="IMovementProfile3D.Configure"/> may overwrite it.</summary>
    public float Gravity { get; set; } = 25f;      // m/s^2 downward
    /// <summary>Terminal fall speed, world units/s (absolute value used). Default 50.</summary>
    public float MaxFallSpeed { get; set; } = 50f; // clamp

    // Grounding/slope
    /// <summary>Extra distance below the capsule searched for ground each probe, world units. Default 0.12.</summary>
    public float GroundProbeDistance { get; set; } = 0.12f;
    /// <summary>Separation kept between the capsule and obstacles during sweeps, world units. Default 0.03.</summary>
    public float SkinWidth { get; set; } = 0.03f;
    /// <summary>Steepest walkable slope in degrees; steeper ground does not count as grounded. Default 60.</summary>
    public float MaxSlopeAngleDeg { get; set; } = 60f;
    /// <summary>Constant downward speed (world units/s) applied while grounded so the controller keeps contact and re-snaps to terrain. Default 2.</summary>
    public float GroundSnapSpeed { get; set; } = 2f; // tiny downward velocity when grounded

    /// <summary>Layers treated as ground for probes and terrain snapping. Default <see cref="PhysxLayer.World"/>.</summary>
    public PhysxLayer GroundMask { get; set; } = PhysxLayer.World;
    // Collision / sweeps
    /// <summary>Sweep the capsule for collide-and-slide and ground probing. When false the body moves without collision or depenetration and grounding falls back to a short downward ray from the feet. Default true.</summary>
    public bool UseCapsuleSweeps { get; set; } = true;
    /// <summary>Maximum collide-and-slide iterations per move (clamped 1..8). Default 3.</summary>
    public int MaxSlideIterations { get; set; } = 3;

    // For movement sweeps, you typically want to collide with World + Dynamic, but not Character/Trigger.
    /// <summary>Layers that block movement sweeps and depenetration. Default <see cref="PhysxLayer.World"/> | <see cref="PhysxLayer.Dynamic"/> (other characters and triggers pass through).</summary>
    public PhysxLayer CollisionMask { get; set; } = PhysxLayer.World | PhysxLayer.Dynamic;

    // Depenetration
    /// <summary>Push the capsule out of overlapping geometry before and after each move (requires <see cref="UseCapsuleSweeps"/>). Default true.</summary>
    public bool UseDepenetration { get; set; } = true;
    /// <summary>Maximum push-out iterations per depenetration pass (clamped 1..16). Default 3.</summary>
    public int DepenetrationIterations { get; set; } = 3;

    // How far we push out per depenetration step when overlap is detected.
    // Typically <= SkinWidth.
    /// <summary>Distance pushed per depenetration iteration, world units; typically &lt;= <see cref="SkinWidth"/>. Default 0.03.</summary>
    public float DepenetrationPushDistance { get; set; } = 0.03f;

    // Character shape for grounding probe.
    /// <summary>Capsule radius used by sweeps and probes, world units; should match the body's collider. Default 0.28.</summary>
    public float Radius { get; set; } = 0.28f;
    /// <summary>Total capsule height (including both hemispheres) used by sweeps and probes, world units. Default 1.8.</summary>
    public float Height { get; set; } = 1.8f;
    /// <summary>Maps input to movement each tick; default <see cref="TpsMovementProfile3D.Default"/>. Assigning a new instance makes the next step call its <see cref="IMovementProfile3D.Configure"/>. When null, input is ignored and the last frame is reused.</summary>
    public IMovementProfile3D? MovementProfile { get; set; } = TpsMovementProfile3D.Default;
    /// <summary>Speeds and turn rate passed to the movement profile. Default <see cref="CharacterMovementStats3D.Default"/>.</summary>
    public CharacterMovementStats3D MovementStats { get; set; } = CharacterMovementStats3D.Default;

    // Outputs
    /// <inheritdoc/>
    public Vector3 Position => _body?.Position ?? Vector3.Zero;
    /// <inheritdoc/>
    public Quaternion Rotation => _body?.Rotation ?? Quaternion.Identity;
    /// <inheritdoc/>
    public float Yaw => _yaw;
    /// <inheritdoc/>
    public bool IsGrounded => _isGrounded;
    /// <inheritdoc/>
    public Vector3 Velocity => _velocity;
    /// <inheritdoc/>
    public CharacterMovementFrame3D LastMovementFrame { get; private set; }
    /// <inheritdoc/>
    public bool HasActiveMovementInput => _movementInput switch
    {
        CharacterClickToMoveInput3D => true,
        CharacterRealtimeWasdInput3D wasd
            => MathF.Abs(wasd.MoveX) >= 0.01f
                || MathF.Abs(wasd.MoveZ) >= 0.01f
                || wasd.Jump,
        _ => false,
    };

    /// <summary>Appends an ability; abilities run in registration order inside every <see cref="Step"/>.</summary>
    /// <param name="ability">Ability to add.</param>
    /// <exception cref="ArgumentNullException"><paramref name="ability"/> is null.</exception>
    public void AddAbility(ICharacterAbility3D ability)
    {
        if (ability == null)
            throw new ArgumentNullException(nameof(ability));
        _abilities.Add(ability);
    }

    /// <summary>Removes all abilities.</summary>
    public void ClearAbilities() => _abilities.Clear();

    /// <inheritdoc/>
    public void SetBody(IPhysxBody3D body)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _yaw = ExtractYaw(body.Rotation);
    }

    /// <summary>
    /// Routes sweeps and probes through <paramref name="queries"/> (e.g. the DI-registered <see cref="ISpatialQueryProvider"/>,
    /// which works with physics on or off) instead of the world's physics engine, and enables slope terrain snapping. See the
    /// class remarks for the obstacle-blocking caveat.
    /// </summary>
    /// <param name="queries">Query provider to use.</param>
    public void SetQueryProvider(ISpatialQueryProvider queries) => _queries = queries;

    private void MoveIntent(float moveX, float moveZ)
        => _moveLocal = new Vector3(moveX, 0f, moveZ);

    private void SprintIntent(bool sprintHeld) => _sprintHeld = sprintHeld;

    private void JumpIntent(bool jumpPressed) => _jumpPressed = jumpPressed;

    private void LookIntent(float lookX, float lookY, float lookZ)
    {
        var fwd = new Vector3(lookX, lookY, lookZ);
        if (fwd.LengthSquared() < 1e-10f)
            return;

        fwd = Vector3.Normalize(fwd);
        _cameraYaw = MathF.Atan2(fwd.X, fwd.Z);

        float pitch = MathF.Asin(Math.Clamp(fwd.Y, -1f, 1f));
        _cameraPitch = Math.Clamp(pitch, -1.55f, 1.55f);
    }

    /// <inheritdoc/>
    public void SetMovementInput(ICharacterMovementInput3D input)
        => _movementInput = input ?? CharacterRealtimeWasdInput3D.Neutral;

    /// <inheritdoc/>
    public void AddLocalMotionDelta(Vector3 localDelta)
    {
        if (localDelta.LengthSquared() <= 1e-12f)
            return;

        _authoredLocalMotionDelta += localDelta;
    }

    /// <inheritdoc/>
    public void Step(float dt, IGameWorldManager3D world)
    {
        if (dt <= 0f || float.IsNaN(dt) || float.IsInfinity(dt))
            return;

        var body = _body;
        if (body == null)
            return;

        ApplyMovementProfile(body);

        // 0) Depenetrate first (handles spawn/teleport/streaming into geometry)
        if (UseDepenetration && UseCapsuleSweeps)
        {
            if (TryDepenetrate(body, world, out var depenNormal))
            {
                // If we pushed out of something, kill velocity INTO that normal
                _velocity -= depenNormal * MathF.Min(0f, Vector3.Dot(_velocity, depenNormal));
            }
        }

        // 1) Ground probe (start-of-step)
        ProbeGround(body, world, out _isGrounded, out _groundNormal);

        ApplyAuthoredLocalMotion(body, world);

        // Authored motion can lift/drop the body, so refresh grounding before
        // resolving the regular movement input for this tick.
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

        var movementFrame = LastMovementFrame;
        float baseSpeed = _sprintHeld ? movementFrame.SprintSpeed : movementFrame.MoveSpeed;
        baseSpeed = MathF.Max(0f, baseSpeed);

        float desiredSpeed = (moveWorld.LengthSquared() > 1e-10f) ? baseSpeed : 0f;

        // 3) Yaw-only rotation
        float targetYaw;
        if (movementFrame.UseExternalFacing)
        {
            targetYaw = movementFrame.ExternalFacingYaw;
        }
        else if (movementFrame.FaceMoveDirection && desiredSpeed > 0f)
        {
            targetYaw = MathF.Atan2(moveWorld.X, moveWorld.Z);
        }
        else
        {
            targetYaw = _cameraYaw;
        }

        _yaw = (movementFrame.RotationSpeedDegPerSec > 0f)
            ? MoveTowardAngleRad(_yaw, targetYaw, (movementFrame.RotationSpeedDegPerSec * (MathF.PI / 180f)) * dt)
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
        // When grounded we keep a small constant downward velocity so the
        // sweep produces displacement.Y < 0 and ResolveDownwardTerrainContact
        // resamples the ground Y at the new XZ each tick. Without this, walking
        // across a slope leaves the body at the spawn-time Y (drift).
        // Abilities like SimpleJumpAbility set IsGrounded=false before this
        // block so jumping is unaffected.
        if (ctx.IsGrounded)
        {
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
            newVH = MoveToward(vH, Vector3.Zero, decel * dt);
        else
            newVH = MoveToward(vH, targetVH, accel * dt);

        ctx.Velocity = new Vector3(newVH.X, ctx.Velocity.Y, newVH.Z);

        // 7) Apply kinematic motion (sweep + slide)
        var displacement = ctx.Velocity * dt;

        if (UseCapsuleSweeps && displacement.LengthSquared() > 1e-12f)
        {
            body.Position = MoveWithSweepsAndSlide(body, world, displacement, ref ctx.Velocity);
        }
        else
        {
            body.Position += displacement;
        }

        // Optional: post-move depenetration (handles numerical issues & dynamic pushes)
        if (UseDepenetration && UseCapsuleSweeps)
        {
            if (TryDepenetrate(body, world, out var depenNormal))
            {
                // Remove velocity into the push direction (prevents re-penetration jitter)
                ctx.Velocity -= depenNormal * MathF.Min(0f, Vector3.Dot(ctx.Velocity, depenNormal));
            }
        }

        // For replication / gameplay reads
        body.LinearVelocity = ctx.Velocity;
        body.AngularVelocity = Vector3.Zero;

        // 8) Store state
        _velocity = ctx.Velocity;

        // Refresh grounded after move
        ProbeGround(body, world, out _isGrounded, out _groundNormal);
    }

    private void ApplyAuthoredLocalMotion(IPhysxBody3D body, IGameWorldManager3D world)
    {
        var localDelta = _authoredLocalMotionDelta;
        _authoredLocalMotionDelta = Vector3.Zero;

        if (localDelta.LengthSquared() <= 1e-12f)
            return;

        float sin = MathF.Sin(_yaw);
        float cos = MathF.Cos(_yaw);
        var displacement = new Vector3(
            (localDelta.X * cos) + (localDelta.Z * sin),
            localDelta.Y,
            (-localDelta.X * sin) + (localDelta.Z * cos));

        if (UseCapsuleSweeps && displacement.LengthSquared() > 1e-12f)
        {
            var authoredVelocity = Vector3.Zero;
            body.Position = MoveWithSweepsAndSlide(body, world, displacement, ref authoredVelocity);
        }
        else
        {
            body.Position += displacement;
        }
    }

    private void ApplyMovementProfile(IPhysxBody3D body)
    {
        var profile = MovementProfile;
        if (profile == null)
            return;

        if (!ReferenceEquals(_configuredMovementProfile, profile))
        {
            profile.Configure(this);
            _configuredMovementProfile = profile;
        }

        var frame = profile.Evaluate(_movementInput, body.Position, MovementStats);
        LastMovementFrame = frame;
        if (frame.ArrivedAtTarget && _movementInput is CharacterClickToMoveInput3D)
            _movementInput = CharacterRealtimeWasdInput3D.Neutral;

        LookIntent(frame.LookDirection.X, frame.LookDirection.Y, frame.LookDirection.Z);
        MoveIntent(frame.LocalMoveX, frame.LocalMoveZ);
        SprintIntent(frame.Sprint);
        JumpIntent(frame.Jump);
    }

    private Vector3 MoveWithSweepsAndSlide(IPhysxBody3D body, IGameWorldManager3D world, Vector3 displacement, ref Vector3 velocity)
    {
        float halfLength = MathF.Max(0f, (Height * 0.5f) - Radius);

        var pos = body.Position;
        var remaining = displacement;

        int iters = Math.Clamp(MaxSlideIterations, 1, 8);

        for (int iter = 0; iter < iters; iter++)
        {
            float dist = remaining.Length();
            if (dist <= 1e-6f)
                break;

            var dir = remaining / dist;

            // Cast slightly farther to preserve SkinWidth separation
            float castDist = dist + SkinWidth;

            var hits = QueryCapsuleCast(world,
                center: pos,
                radius: Radius,
                halfLength: halfLength,
                direction: dir,
                maxDistance: castDist,
                maxHits: 8,
                layerMask: (uint)CollisionMask
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
                pos += remaining;
                ResolveDownwardTerrainContact(world, ref pos, remaining, ref velocity);
                break;
            }

            // Normalize normal defensively
            var n = best.Normal;
            if (n.LengthSquared() > 1e-10f)
                n = Vector3.Normalize(n);
            else
                n = Vector3.UnitY;

            // If we are blocked immediately (T <= 0), treat as overlap / initial penetration.
            // Push out a small amount and continue.
            if (UseDepenetration && best.T <= 1e-6f)
            {
                float push = MathF.Max(1e-4f, MathF.Min(DepenetrationPushDistance, MathF.Max(SkinWidth, DepenetrationPushDistance)));
                pos += n * push;

                // Remove velocity into the normal to prevent vibrating into it
                float vn = Vector3.Dot(velocity, n);
                if (vn < 0f)
                    velocity -= n * vn;

                // Try next iteration with same remaining displacement
                continue;
            }

            // Move up to impact, leaving SkinWidth
            float travel = MathF.Max(0f, best.T - SkinWidth);
            travel = MathF.Min(travel, dist);

            pos += dir * travel;

            float leftover = dist - travel;
            if (leftover <= 1e-6f)
                break;

            // Slide: remove component into the hit normal
            var leftoverVec = dir * leftover;
            var slid = leftoverVec - n * Vector3.Dot(leftoverVec, n);

            // Also remove velocity component into the normal (prevents "sticking" acceleration into wall)
            float vInto = Vector3.Dot(velocity, n);
            if (vInto < 0f)
                velocity -= n * vInto;

            if (slid.LengthSquared() <= 1e-12f)
                break;

            remaining = slid;
        }

        return pos;
    }

    private void ResolveDownwardTerrainContact(
        IGameWorldManager3D world,
        ref Vector3 position,
        Vector3 displacement,
        ref Vector3 velocity)
    {
        if (_queries == null || displacement.Y >= 0f)
            return;

        float halfLength = MathF.Max(0f, (Height * 0.5f) - Radius);
        float centerToFoot = halfLength + Radius;
        float probeUp = MathF.Max(SkinWidth + GroundProbeDistance, 0.05f);

        // Slope-aware probe: walking horizontally `h` units along a slope of angle θ
        // moves the ground by `h·tanθ` per tick — UP if walking uphill, DOWN if
        // downhill. We size the budget at tan(85°)≈11.4 ("any non-vertical slope
        // sticks") because heightmap terrain has no genuine vertical walls, only
        // steep ramps. Real cliffs still work: their drop is much larger than the
        // budget, so the snap condition below treats them as a fall.
        //
        // The probe origin sits ABOVE the body center (not at the foot) so the
        // downward ray clears any rising terrain. Walking UP a 60° slope at 5 m/s
        // raises the ground by 0.35 per tick — a probe origin near the foot would
        // start below the new ground and miss it, leaving the body embedded.
        float horizMag = MathF.Sqrt(displacement.X * displacement.X + displacement.Z * displacement.Z);
        const float StickSlopeTan = 11.43f; // tan(85°)
        float slopeProbe = horizMag * StickSlopeTan;
        float probeDown = centerToFoot + slopeProbe + probeUp + SkinWidth;
        var origin = new Vector3(position.X, position.Y + probeUp, position.Z);

        foreach (var hit in QueryRayCast(
            world,
            new PhysxRay3D(origin, origin - (Vector3.UnitY * probeDown)),
            maxHits: 4,
            layerMask: (uint)GroundMask))
        {
            if (hit.Body != null)
                continue;

            if (hit.Normal.Y < 0.2f)
                continue;

            float groundedCenterY = hit.Point.Y + centerToFoot;
            // Snap to ground if body is at or below it (depenetration), OR if body
            // is above it within the slope-step distance — this is the "follow the
            // slope down" case. Without the second clause, walking down any slope
            // leaves the body floating until gravity pulls it through.
            float aboveBy = position.Y - groundedCenterY;
            if (aboveBy <= slopeProbe + probeUp)
            {
                position = new Vector3(position.X, groundedCenterY, position.Z);
                if (velocity.Y < 0f)
                    velocity = new Vector3(velocity.X, 0f, velocity.Z);
            }
            // No snap → body is above the found terrain by more than a slope-step.
            // That's a genuine drop (jump apex, cliff, etc.); gravity will pull
            // the body down and a future tick's larger displacement.Y will reach
            // the terrain through this same probe.

            return;
        }
    }

    private bool TryDepenetrate(IPhysxBody3D body, IGameWorldManager3D world, out Vector3 depenNormal)
    {
        depenNormal = Vector3.Zero;

        float halfLength = MathF.Max(0f, (Height * 0.5f) - Radius);
        int iters = Math.Clamp(DepenetrationIterations, 1, 16);

        var pos = body.Position;
        bool moved = false;

        for (int i = 0; i < iters; i++)
        {
            // We use a tiny sweep in multiple directions to find “blocking” normals at T==0-ish.
            // Direction set: up/down + 4 cardinals. Cheap and effective.
            Span<Vector3> dirs =
            [
                Vector3.UnitY,
                -Vector3.UnitY,
                Vector3.UnitX,
                -Vector3.UnitX,
                Vector3.UnitZ,
                -Vector3.UnitZ
            ];

            bool pushedThisIter = false;

            for (int d = 0; d < dirs.Length; d++)
            {
                var dir = dirs[d];

                var hits = QueryCapsuleCast(world,
                    center: pos,
                    radius: Radius,
                    halfLength: halfLength,
                    direction: dir,
                    maxDistance: MathF.Max(SkinWidth, DepenetrationPushDistance),
                    maxHits: 4,
                    layerMask: (uint)CollisionMask
                );

                foreach (var h in hits)
                {
                    if (h.Body == null)
                        continue;
                    if (ReferenceEquals(h.Body, body))
                        continue;

                    // Only treat near-zero hits as overlap-like.
                    if (h.T > 1e-4f)
                        continue;

                    var n = h.Normal;
                    if (n.LengthSquared() > 1e-10f)
                        n = Vector3.Normalize(n);
                    else
                        n = Vector3.UnitY;

                    float push = MathF.Max(1e-4f, MathF.Min(DepenetrationPushDistance, MathF.Max(SkinWidth, DepenetrationPushDistance)));
                    pos += n * push;

                    depenNormal = n;
                    pushedThisIter = true;
                    moved = true;
                    break;
                }

                if (pushedThisIter)
                    break;
            }

            if (!pushedThisIter)
                break;
        }

        if (moved)
            body.Position = pos;

        return moved;
    }

    private void ProbeGround(IPhysxBody3D body, IGameWorldManager3D world, out bool grounded, out Vector3 normal)
    {
        // Capsule geometry:
        // Your body.Position is the capsule center (BEPU body center).
        // halfLength matches your "capsule segment half length" (height*0.5 - radius).
        float halfLength = MathF.Max(0f, (Height * 0.5f) - Radius);

        // Sweep distance: skin + probe
        float maxDist = MathF.Max(0f, GroundProbeDistance + SkinWidth);

        // Down direction
        var dir = -Vector3.UnitY;

        // Get hits
        var hits = UseCapsuleSweeps
            ? QueryCapsuleCast(world,
                center: body.Position,
                radius: Radius,
                halfLength: halfLength,
                direction: dir,
                maxDistance: maxDist,
                maxHits: 4,
                layerMask: (uint)GroundMask
            )
            : Enumerable.Empty<PhysxRaycastHit3D>();

        bool gotHit = false;
        PhysxRaycastHit3D best = default;

        foreach (var h in hits)
        {
            if (h.Body != null && ReferenceEquals(h.Body, body))
                continue;

            best = h;
            gotHit = true;
            break; // hits are already sorted by T in engine
        }

        if (!gotHit)
        {
            float probeUp = MathF.Max(SkinWidth, 0.03f);
            float centerToFoot = halfLength + Radius;
            var origin = body.Position - (Vector3.UnitY * centerToFoot) + (Vector3.UnitY * probeUp);
            var target = origin - (Vector3.UnitY * (GroundProbeDistance + SkinWidth + probeUp));

            foreach (var h in QueryRayCast(
                world,
                new PhysxRay3D(origin, target),
                maxHits: 4,
                layerMask: (uint)GroundMask))
            {
                if (h.Body != null && ReferenceEquals(h.Body, body))
                    continue;

                best = h;
                gotHit = true;
                break;
            }
        }

        if (!gotHit)
        {
            grounded = false;
            normal = Vector3.UnitY;
            return;
        }

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

    // ── Query delegation: ISpatialQueryProvider if set, else direct PhysxWorld ──

    private IEnumerable<PhysxRaycastHit3D> QueryCapsuleCast(
        IGameWorldManager3D world,
        Vector3 center, float radius, float halfLength,
        Vector3 direction, float maxDistance, int maxHits, uint layerMask)
    {
        if (_queries != null)
        {
            // Convert SpatialHit → PhysxRaycastHit3D for compatibility
            foreach (var h in _queries.CapsuleCast(center, radius, halfLength, direction, maxDistance, maxHits, layerMask))
                yield return new PhysxRaycastHit3D(null!, h.Point, h.Normal, h.T);
            yield break;
        }

        // Fallback: direct physics engine (backward compat)
        if (world.PhysxWorld?.Engine != null)
        {
            foreach (var h in world.PhysxWorld.Engine.CapsuleCast(center, radius, halfLength, direction, maxDistance, maxHits, layerMask))
                yield return h;
        }
    }

    private IEnumerable<PhysxRaycastHit3D> QueryRayCast(
        IGameWorldManager3D world,
        PhysxRay3D ray, int maxHits, uint layerMask)
    {
        if (_queries != null)
        {
            var dir = Vector3.Normalize(ray.To - ray.From);
            var dist = Vector3.Distance(ray.From, ray.To);
            foreach (var h in _queries.RayCast(ray.From, dir, dist, maxHits, layerMask))
                yield return new PhysxRaycastHit3D(null!, h.Point, h.Normal, h.T);
            yield break;
        }

        if (world.PhysxWorld?.Engine != null)
        {
            foreach (var h in world.PhysxWorld.Engine.RayCast(ray, maxHits, layerMask))
                yield return h;
        }
    }

    private static float ExtractYaw(Quaternion q)
    {
        float siny = 2f * (q.W * q.Y + q.X * q.Z);
        float cosy = 1f - 2f * (q.Y * q.Y + q.X * q.X);
        return MathF.Atan2(siny, cosy);
    }
}

/// <summary>
/// Minimal jump <see cref="ICharacterAbility3D"/>: on the rising edge of the jump input while grounded, sets vertical velocity
/// to <see cref="JumpSpeed"/> and marks the character airborne. No coyote time, buffering or double jump. Holds per-character
/// edge state, so use one instance per controller.
/// </summary>
public sealed class SimpleJumpAbility3D : ICharacterAbility3D
{
    /// <summary>Upward launch speed in world units per second. Default 7.5.</summary>
    public float JumpSpeed { get; set; } = 7.5f;

    private bool _wasPressed;

    /// <inheritdoc/>
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
