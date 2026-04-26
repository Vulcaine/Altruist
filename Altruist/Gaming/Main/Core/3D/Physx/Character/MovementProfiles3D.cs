using System.Numerics;

namespace Altruist.Gaming.ThreeD;

public interface IMovementProfile3D
{
    void Configure(KinematicCharacterController3D controller);
    CharacterMovementFrame3D Evaluate(ICharacterMovementInput3D input, Vector3 currentPosition, in CharacterMovementStats3D stats);
}

public interface ICharacterMovementInput3D
{
}

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

public readonly record struct CharacterClickToMoveInput3D(
    Vector3 TargetWorldPosition,
    float ArrivalDistance,
    bool Sprint = false) : ICharacterMovementInput3D;

public readonly record struct CharacterMovementStats3D(
    float WalkSpeed,
    float SprintSpeed,
    float MoveSpeedMultiplier,
    float RotationSpeedDegPerSec)
{
    public static CharacterMovementStats3D Default => new(
        WalkSpeed: 5f,
        SprintSpeed: 5f,
        MoveSpeedMultiplier: 1f,
        RotationSpeedDegPerSec: 0f);
}

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

public sealed class MmoMovementProfile3D : IMovementProfile3D
{
    public static MmoMovementProfile3D Default { get; } = new();

    public void Configure(KinematicCharacterController3D controller)
    {
        controller.Gravity = 0f;
        controller.MaxFallSpeed = 0f;
        controller.GroundSnapSpeed = 0f;
    }

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

    public static Vector3 ResolveLookDirection(in CharacterRealtimeWasdInput3D input)
    {
        if (input.LookDirection.LengthSquared() > 1e-10f)
            return input.LookDirection;

        return new Vector3(MathF.Sin(input.CameraYaw), 0f, MathF.Cos(input.CameraYaw));
    }

    public static float ResolveCameraYaw(in CharacterRealtimeWasdInput3D input)
    {
        if (input.LookDirection.LengthSquared() > 1e-10f)
        {
            var look = Vector3.Normalize(input.LookDirection);
            return MathF.Atan2(look.X, look.Z);
        }

        return input.CameraYaw;
    }

    public static float ResolveSpeedMultiplier(in CharacterMovementStats3D stats)
        => MathF.Max(0f, stats.MoveSpeedMultiplier);
}

public sealed class TpsMovementProfile3D : IMovementProfile3D
{
    public static TpsMovementProfile3D Default { get; } = new();

    public void Configure(KinematicCharacterController3D controller)
    {
        controller.Gravity = 25f;
        controller.MaxFallSpeed = 50f;
        controller.GroundSnapSpeed = 2f;
    }

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

public sealed class FpsMovementProfile3D : IMovementProfile3D
{
    public static FpsMovementProfile3D Default { get; } = new();

    public void Configure(KinematicCharacterController3D controller)
    {
        controller.Gravity = 25f;
        controller.MaxFallSpeed = 50f;
        controller.GroundSnapSpeed = 2f;
    }

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
