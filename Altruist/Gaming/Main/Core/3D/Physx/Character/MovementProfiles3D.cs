using System.Numerics;

namespace Altruist.Gaming.ThreeD;

/// <summary>
/// Strategy that translates a raw movement input (<see cref="ICharacterMovementInput3D"/>) into the per-tick
/// <see cref="CharacterMovementFrame3D"/> a <see cref="KinematicCharacterController3D"/> executes, and tunes the controller's
/// vertical settings once when it is assigned.
/// </summary>
/// <remarks>
/// Built-in profiles: <see cref="TpsMovementProfile3D"/> (third-person, character turns to face its move direction; the
/// controller default), <see cref="FpsMovementProfile3D"/> (first-person, character faces the camera/look yaw and strafes)
/// and <see cref="MmoMovementProfile3D"/> (like TPS but honours an explicit external facing yaw). All three also accept
/// <see cref="CharacterClickToMoveInput3D"/>. Implement your own profile for other control schemes; keep
/// <see cref="Evaluate"/> free of side effects because it runs every tick on the simulation thread.
/// </remarks>
public interface IMovementProfile3D
{
    /// <summary>
    /// Applies profile-specific controller settings (gravity, fall speed, ground snap). Called once by the controller on the
    /// first <see cref="KinematicCharacterController3D.Step"/> after the profile is assigned to
    /// <see cref="KinematicCharacterController3D.MovementProfile"/> (overwrites values set earlier on the controller).
    /// </summary>
    /// <param name="controller">Controller being configured.</param>
    void Configure(KinematicCharacterController3D controller);
    /// <summary>Converts the current input into this tick's movement frame.</summary>
    /// <param name="input">Latest input given to <see cref="KinematicCharacterController3D.SetMovementInput"/>; unknown input types are treated as neutral.</param>
    /// <param name="currentPosition">Body position at the start of the tick (used by click-to-move).</param>
    /// <param name="stats">Per-character speed/turn stats from <see cref="KinematicCharacterController3D.MovementStats"/>.</param>
    /// <returns>The frame the controller will execute this tick.</returns>
    CharacterMovementFrame3D Evaluate(ICharacterMovementInput3D input, Vector3 currentPosition, in CharacterMovementStats3D stats);
}

/// <summary>
/// Marker interface for inputs accepted by <see cref="KinematicCharacterController3D.SetMovementInput"/>. Built-ins:
/// <see cref="CharacterRealtimeWasdInput3D"/> (continuous stick/WASD) and <see cref="CharacterClickToMoveInput3D"/> (walk to a
/// point). Custom inputs need a custom <see cref="IMovementProfile3D"/> that recognises them.
/// </summary>
public interface ICharacterMovementInput3D
{
}

/// <summary>
/// Continuous (per-tick) movement input: a camera-relative move axis plus look/facing, sprint and jump. The input is
/// latched: it keeps applying every tick until replaced via <see cref="KinematicCharacterController3D.SetMovementInput"/>.
/// </summary>
/// <param name="MoveX">Strafe axis, -1 (left) .. +1 (right), relative to the camera yaw.</param>
/// <param name="MoveZ">Forward axis, -1 (back) .. +1 (forward), relative to the camera yaw. The combined axis is normalised, so analog magnitude does not scale speed.</param>
/// <param name="LookDirection">Camera forward in world space (+Y up, yaw 0 = +Z). If (near) zero, <paramref name="CameraYaw"/> is used instead.</param>
/// <param name="CameraYaw">Camera yaw in radians (0 = +Z, +PI/2 = +X); fallback when <paramref name="LookDirection"/> is zero.</param>
/// <param name="ExternalFacingYaw">Explicit character facing yaw in radians, used when <paramref name="UseExternalFacing"/> is true (honoured only by <see cref="MmoMovementProfile3D"/>).</param>
/// <param name="UseExternalFacing">Face <paramref name="ExternalFacingYaw"/> instead of the move/camera direction.</param>
/// <param name="Sprint">Use sprint speed instead of walk speed.</param>
/// <param name="Jump">Jump button held; jump abilities such as <see cref="SimpleJumpAbility3D"/> trigger on the rising edge.</param>
public readonly record struct CharacterRealtimeWasdInput3D(
    float MoveX,
    float MoveZ,
    Vector3 LookDirection,
    float CameraYaw,
    float ExternalFacingYaw,
    bool UseExternalFacing,
    bool Sprint,
    bool Jump) : ICharacterMovementInput3D
{
    /// <summary>No movement, looking along +Z, no sprint/jump. Used when the controller has no input.</summary>
    public static CharacterRealtimeWasdInput3D Neutral => new(
        MoveX: 0f,
        MoveZ: 0f,
        LookDirection: Vector3.UnitZ,
        CameraYaw: 0f,
        ExternalFacingYaw: 0f,
        UseExternalFacing: false,
        Sprint: false,
        Jump: false);
}

/// <summary>
/// Click-to-move input: walk on the XZ plane toward a world point and face it. Once within
/// <paramref name="ArrivalDistance"/> (horizontal distance) the controller reports
/// <see cref="CharacterMovementFrame3D.ArrivedAtTarget"/> and resets its input to <see cref="CharacterRealtimeWasdInput3D.Neutral"/>.
/// </summary>
/// <remarks>Straight-line only (no pathfinding); for routed movement steer along a path yourself and feed waypoints.</remarks>
/// <param name="TargetWorldPosition">Destination in world space (its Y is ignored).</param>
/// <param name="ArrivalDistance">Horizontal distance at which the target counts as reached, in world units.</param>
/// <param name="Sprint">Move at sprint speed.</param>
public readonly record struct CharacterClickToMoveInput3D(
    Vector3 TargetWorldPosition,
    float ArrivalDistance,
    bool Sprint = false) : ICharacterMovementInput3D;

/// <summary>Per-character movement stats fed to the active <see cref="IMovementProfile3D"/> via <see cref="KinematicCharacterController3D.MovementStats"/>.</summary>
/// <param name="WalkSpeed">Base move speed in world units per second.</param>
/// <param name="SprintSpeed">Move speed while sprinting, in world units per second.</param>
/// <param name="MoveSpeedMultiplier">Multiplier applied to both speeds (buffs/slows); negative values clamp to 0.</param>
/// <param name="RotationSpeedDegPerSec">Max turn rate in degrees per second; 0 or less snaps to the target yaw instantly.</param>
public readonly record struct CharacterMovementStats3D(
    float WalkSpeed,
    float SprintSpeed,
    float MoveSpeedMultiplier,
    float RotationSpeedDegPerSec)
{
    /// <summary>Walk 5, sprint 5, multiplier 1, instant turning.</summary>
    public static CharacterMovementStats3D Default => new(
        WalkSpeed: 5f,
        SprintSpeed: 5f,
        MoveSpeedMultiplier: 1f,
        RotationSpeedDegPerSec: 0f);
}

/// <summary>
/// Resolved movement for one tick, produced by <see cref="IMovementProfile3D.Evaluate"/> and executed by
/// <see cref="KinematicCharacterController3D"/>; the last one is exposed as <see cref="KinematicCharacterController3D.LastMovementFrame"/>.
/// </summary>
/// <param name="LocalMoveX">Strafe axis relative to the look yaw (-1..1).</param>
/// <param name="LocalMoveZ">Forward axis relative to the look yaw (-1..1).</param>
/// <param name="LookDirection">Camera/look forward in world space; defines the frame the local move axes are relative to.</param>
/// <param name="MoveSpeed">Walk speed after multipliers, world units per second.</param>
/// <param name="SprintSpeed">Sprint speed after multipliers, world units per second.</param>
/// <param name="RotationSpeedDegPerSec">Max turn rate in degrees per second; 0 or less turns instantly.</param>
/// <param name="FaceMoveDirection">Turn toward the movement direction while moving (otherwise face the look yaw).</param>
/// <param name="UseExternalFacing">Turn toward <paramref name="ExternalFacingYaw"/>; takes precedence over <paramref name="FaceMoveDirection"/>.</param>
/// <param name="ExternalFacingYaw">Explicit facing yaw in radians (0 = +Z).</param>
/// <param name="Sprint">Use <paramref name="SprintSpeed"/>.</param>
/// <param name="Jump">Jump button state passed to abilities.</param>
/// <param name="ArrivedAtTarget">Click-to-move target reached this tick.</param>
public readonly record struct CharacterMovementFrame3D(
    float LocalMoveX,
    float LocalMoveZ,
    Vector3 LookDirection,
    float MoveSpeed,
    float SprintSpeed,
    float RotationSpeedDegPerSec,
    bool FaceMoveDirection,
    bool UseExternalFacing,
    float ExternalFacingYaw,
    bool Sprint,
    bool Jump,
    bool ArrivedAtTarget);

/// <summary>
/// MMO-style <see cref="IMovementProfile3D"/>: camera-relative WASD where the character faces its move direction unless the
/// input sets <see cref="CharacterRealtimeWasdInput3D.UseExternalFacing"/>, plus click-to-move. Also hosts the shared helper
/// statics used by the other built-in profiles.
/// </summary>
/// <remarks>
/// Pick this when the client may dictate facing (e.g. targeting/auto-face); use <see cref="TpsMovementProfile3D"/> when the
/// character should always face its movement, or <see cref="FpsMovementProfile3D"/> for camera-locked facing.
/// <see cref="Configure"/> sets gravity 25, max fall speed 50, ground snap 2.
/// </remarks>
public sealed class MmoMovementProfile3D : IMovementProfile3D
{
    /// <summary>Shared stateless instance.</summary>
    public static MmoMovementProfile3D Default { get; } = new();

    // Gravity must stay non-zero so KCC.ResolveDownwardTerrainContact runs each
    // tick: that path is what re-samples ground Y at the new XZ and keeps the
    // server-authoritative pivot snapped to the heightmap. With Gravity=0 the
    // pivot freezes at spawn elevation and drifts away from the client visual
    // (server-debug gizmos render below the terrain on slopes).
    /// <inheritdoc/>
    /// <remarks>Sets <c>Gravity = 25</c>, <c>MaxFallSpeed = 50</c>, <c>GroundSnapSpeed = 2</c>. Gravity must stay non-zero so the controller keeps re-snapping to terrain on slopes.</remarks>
    public void Configure(KinematicCharacterController3D controller)
    {
        controller.Gravity = 25f;
        controller.MaxFallSpeed = 50f;
        controller.GroundSnapSpeed = 2f;
    }

    /// <inheritdoc/>
    public CharacterMovementFrame3D Evaluate(ICharacterMovementInput3D input, Vector3 currentPosition, in CharacterMovementStats3D stats)
    {
        if (input is CharacterClickToMoveInput3D click)
            return EvaluateClickToMove(click, currentPosition, stats);

        var realtime = input is CharacterRealtimeWasdInput3D wasd
            ? wasd
            : CharacterRealtimeWasdInput3D.Neutral;

        float speedMultiplier = ResolveSpeedMultiplier(stats);

        return new CharacterMovementFrame3D(
            realtime.MoveX,
            realtime.MoveZ,
            ResolveLookDirection(realtime),
            MathF.Max(0f, stats.WalkSpeed * speedMultiplier),
            MathF.Max(0f, stats.SprintSpeed * speedMultiplier),
            stats.RotationSpeedDegPerSec,
            FaceMoveDirection: !realtime.UseExternalFacing,
            UseExternalFacing: realtime.UseExternalFacing,
            ExternalFacingYaw: realtime.ExternalFacingYaw,
            Sprint: realtime.Sprint,
            Jump: realtime.Jump,
            ArrivedAtTarget: false);
    }

    private static CharacterMovementFrame3D EvaluateClickToMove(
        in CharacterClickToMoveInput3D input,
        Vector3 currentPosition,
        in CharacterMovementStats3D stats)
    {
        var delta = input.TargetWorldPosition - currentPosition;
        delta.Y = 0f;

        float distance = delta.Length();
        float speedMultiplier = ResolveSpeedMultiplier(stats);
        if (distance <= MathF.Max(0f, input.ArrivalDistance))
        {
            return new CharacterMovementFrame3D(
                0f,
                0f,
                Vector3.UnitZ,
                MathF.Max(0f, stats.WalkSpeed * speedMultiplier),
                MathF.Max(0f, stats.SprintSpeed * speedMultiplier),
                stats.RotationSpeedDegPerSec,
                FaceMoveDirection: false,
                UseExternalFacing: false,
                ExternalFacingYaw: 0f,
                Sprint: false,
                Jump: false,
                ArrivedAtTarget: true);
        }

        var world = delta / distance;
        float yaw = MathF.Atan2(world.X, world.Z);
        return new CharacterMovementFrame3D(
            0f,
            1f,
            world,
            MathF.Max(0f, stats.WalkSpeed * speedMultiplier),
            MathF.Max(0f, stats.SprintSpeed * speedMultiplier),
            stats.RotationSpeedDegPerSec,
            FaceMoveDirection: false,
            UseExternalFacing: true,
            ExternalFacingYaw: yaw,
            Sprint: input.Sprint,
            Jump: false,
            ArrivedAtTarget: false);
    }

    /// <summary>Returns <see cref="CharacterRealtimeWasdInput3D.LookDirection"/> if non-zero (not normalised), else the forward vector of <see cref="CharacterRealtimeWasdInput3D.CameraYaw"/>.</summary>
    /// <param name="input">Realtime input.</param>
    /// <returns>Look direction in world space.</returns>
    public static Vector3 ResolveLookDirection(in CharacterRealtimeWasdInput3D input)
    {
        if (input.LookDirection.LengthSquared() > 1e-10f)
            return input.LookDirection;

        return new Vector3(MathF.Sin(input.CameraYaw), 0f, MathF.Cos(input.CameraYaw));
    }

    /// <summary>Yaw in radians (0 = +Z, atan2(x, z)) of <see cref="CharacterRealtimeWasdInput3D.LookDirection"/> if non-zero, else <see cref="CharacterRealtimeWasdInput3D.CameraYaw"/>.</summary>
    /// <param name="input">Realtime input.</param>
    /// <returns>Camera yaw in radians.</returns>
    public static float ResolveCameraYaw(in CharacterRealtimeWasdInput3D input)
    {
        if (input.LookDirection.LengthSquared() > 1e-10f)
        {
            var look = Vector3.Normalize(input.LookDirection);
            return MathF.Atan2(look.X, look.Z);
        }

        return input.CameraYaw;
    }

    /// <summary><see cref="CharacterMovementStats3D.MoveSpeedMultiplier"/> clamped to be non-negative.</summary>
    /// <param name="stats">Movement stats.</param>
    /// <returns>Speed multiplier (&gt;= 0).</returns>
    public static float ResolveSpeedMultiplier(in CharacterMovementStats3D stats)
        => MathF.Max(0f, stats.MoveSpeedMultiplier);
}

/// <summary>
/// Third-person <see cref="IMovementProfile3D"/> and the <see cref="KinematicCharacterController3D"/> default: camera-relative
/// WASD where the character turns to face its move direction while moving and turns to the camera yaw when idle; external
/// facing is ignored. Click-to-move input is delegated to <see cref="MmoMovementProfile3D"/>.
/// </summary>
/// <remarks>Use <see cref="FpsMovementProfile3D"/> for camera-locked facing or <see cref="MmoMovementProfile3D"/> to honour an explicit facing yaw. <see cref="Configure"/> sets gravity 25, max fall speed 50, ground snap 2.</remarks>
public sealed class TpsMovementProfile3D : IMovementProfile3D
{
    /// <summary>Shared stateless instance.</summary>
    public static TpsMovementProfile3D Default { get; } = new();

    /// <inheritdoc/>
    public void Configure(KinematicCharacterController3D controller)
    {
        controller.Gravity = 25f;
        controller.MaxFallSpeed = 50f;
        controller.GroundSnapSpeed = 2f;
    }

    /// <inheritdoc/>
    public CharacterMovementFrame3D Evaluate(ICharacterMovementInput3D input, Vector3 currentPosition, in CharacterMovementStats3D stats)
    {
        if (input is CharacterClickToMoveInput3D)
            return MmoMovementProfile3D.Default.Evaluate(input, currentPosition, stats);

        var realtime = input is CharacterRealtimeWasdInput3D wasd
            ? wasd
            : CharacterRealtimeWasdInput3D.Neutral;

        float speedMultiplier = MmoMovementProfile3D.ResolveSpeedMultiplier(stats);
        return new CharacterMovementFrame3D(
            realtime.MoveX,
            realtime.MoveZ,
            MmoMovementProfile3D.ResolveLookDirection(realtime),
            MathF.Max(0f, stats.WalkSpeed * speedMultiplier),
            MathF.Max(0f, stats.SprintSpeed * speedMultiplier),
            stats.RotationSpeedDegPerSec,
            FaceMoveDirection: true,
            UseExternalFacing: false,
            ExternalFacingYaw: realtime.ExternalFacingYaw,
            Sprint: realtime.Sprint,
            Jump: realtime.Jump,
            ArrivedAtTarget: false);
    }
}

/// <summary>
/// First-person <see cref="IMovementProfile3D"/>: the character always faces the camera/look yaw and strafes with WASD;
/// external facing is ignored. Click-to-move input is delegated to <see cref="MmoMovementProfile3D"/>.
/// </summary>
/// <remarks>Use <see cref="TpsMovementProfile3D"/> when the character should turn toward its movement instead. <see cref="Configure"/> sets gravity 25, max fall speed 50, ground snap 2.</remarks>
public sealed class FpsMovementProfile3D : IMovementProfile3D
{
    /// <summary>Shared stateless instance.</summary>
    public static FpsMovementProfile3D Default { get; } = new();

    /// <inheritdoc/>
    public void Configure(KinematicCharacterController3D controller)
    {
        controller.Gravity = 25f;
        controller.MaxFallSpeed = 50f;
        controller.GroundSnapSpeed = 2f;
    }

    /// <inheritdoc/>
    public CharacterMovementFrame3D Evaluate(ICharacterMovementInput3D input, Vector3 currentPosition, in CharacterMovementStats3D stats)
    {
        if (input is CharacterClickToMoveInput3D)
            return MmoMovementProfile3D.Default.Evaluate(input, currentPosition, stats);

        var realtime = input is CharacterRealtimeWasdInput3D wasd
            ? wasd
            : CharacterRealtimeWasdInput3D.Neutral;

        float speedMultiplier = MmoMovementProfile3D.ResolveSpeedMultiplier(stats);
        return new CharacterMovementFrame3D(
            realtime.MoveX,
            realtime.MoveZ,
            MmoMovementProfile3D.ResolveLookDirection(realtime),
            MathF.Max(0f, stats.WalkSpeed * speedMultiplier),
            MathF.Max(0f, stats.SprintSpeed * speedMultiplier),
            stats.RotationSpeedDegPerSec,
            FaceMoveDirection: false,
            UseExternalFacing: false,
            ExternalFacingYaw: realtime.ExternalFacingYaw,
            Sprint: realtime.Sprint,
            Jump: realtime.Jump,
            ArrivedAtTarget: false);
    }
}
