using System.Numerics;

using Altruist.Physx.ThreeD;

namespace Altruist.Physx;

/// <summary>
/// Force/impulse verbs on a 3D body, routed through <see cref="Altruist.Physx.Contracts.IPhysxBody.ApplyForce"/> with a
/// <see cref="Altruist.Physx.Contracts.PhysxForce"/>. Each call wakes the body (BEPU backend).
/// </summary>
/// <remarks>
/// Use <see cref="Impulse"/> for one-shot kicks, <see cref="Apply"/> for per-tick continuous forces, and
/// <see cref="IPhysxMotionApi3D"/> to read or overwrite velocity directly. For intent-level helpers (launch along a
/// direction, move toward a point) see <see cref="BodySteeringExtensions3D"/>.
/// </remarks>
public interface IPhysxForceApi3D
{
    /// <summary>
    /// Adds a continuous force for one step: the BEPU backend applies the linear impulse <c>force * FixedDeltaTime</c>
    /// once per call (not once per sub-step), so call it once per fixed step.
    /// </summary>
    /// <param name="body">Target body.</param>
    /// <param name="force">Force in mass·units/s².</param>
    void Apply(IPhysxBody3D body, Vector3 force);

    /// <summary>Adds an instantaneous linear impulse (changes velocity by <c>impulse / mass</c>).</summary>
    /// <param name="body">Target body.</param>
    /// <param name="impulse">Impulse in mass·units/s.</param>
    void Impulse(IPhysxBody3D body, Vector3 impulse);

    /// <summary>Adds an instantaneous angular impulse (not scaled by the time step, despite the name).</summary>
    /// <param name="body">Target body.</param>
    /// <param name="torque">Angular impulse, world-space axis × magnitude.</param>
    void Torque(IPhysxBody3D body, Vector3 torque);

    /// <summary>Overwrites the linear velocity via a <see cref="Altruist.Physx.Contracts.PhysxForce"/> command (for engines that prefer that path).</summary>
    /// <param name="body">Target body.</param>
    /// <param name="velocity">New velocity in units per second.</param>
    void SetLinearVelocity(IPhysxBody3D body, Vector3 velocity);

    /// <summary>Overwrites the angular velocity via a <see cref="Altruist.Physx.Contracts.PhysxForce"/> command.</summary>
    /// <param name="body">Target body.</param>
    /// <param name="angularVelocity">New angular velocity in radians per second.</param>
    void SetAngularVelocity(IPhysxBody3D body, Vector3 angularVelocity);
}

/// <summary>Direct velocity read/write on a 3D body (through the body properties, not force commands).</summary>
public interface IPhysxMotionApi3D
{
    /// <summary>Returns the linear velocity in units per second.</summary>
    /// <param name="body">Body to read.</param>
    Vector3 GetLinearVelocity(IPhysxBody3D body);
    /// <summary>Returns the angular velocity in radians per second.</summary>
    /// <param name="body">Body to read.</param>
    Vector3 GetAngularVelocity(IPhysxBody3D body);

    /// <summary>Overwrites the linear velocity (wakes the body in the BEPU backend).</summary>
    /// <param name="body">Target body.</param>
    /// <param name="velocity">New velocity in units per second.</param>
    void SetLinearVelocity(IPhysxBody3D body, Vector3 velocity);
    /// <summary>Overwrites the angular velocity (wakes the body in the BEPU backend).</summary>
    /// <param name="body">Target body.</param>
    /// <param name="angularVelocity">New angular velocity in radians per second.</param>
    void SetAngularVelocity(IPhysxBody3D body, Vector3 angularVelocity);
}

/// <summary>Pose read/write on a 3D body.</summary>
public interface IPhysxTransformApi3D
{
    /// <summary>Moves the body instantly to a new pose, optionally zeroing linear and angular velocity.</summary>
    /// <param name="body">Target body.</param>
    /// <param name="position">New world position.</param>
    /// <param name="orientation">New world orientation.</param>
    /// <param name="clearVelocities">When <see langword="true"/>, also sets both velocities to zero.</param>
    void Teleport(IPhysxBody3D body, Vector3 position, Quaternion orientation, bool clearVelocities = false);

    /// <summary>Returns the body's current world position and orientation.</summary>
    /// <param name="body">Body to read.</param>
    (Vector3 position, Quaternion orientation) GetPose(IPhysxBody3D body);
}

/// <summary>
/// Entry point for low-level 3D body manipulation, grouping <see cref="Force"/>, <see cref="Motion"/> and
/// <see cref="Transform"/> APIs. Resolve it from DI (BEPU: <see cref="BepuPhysxApiProvider3D"/>, registered when
/// <c>altruist:environment:mode</c> is <c>3D</c>).
/// </summary>
/// <example>
/// <code>
/// physx.Force.Impulse(body, new Vector3(0, 5f, 0));
/// physx.Transform.Teleport(body, spawnPoint, Quaternion.Identity, clearVelocities: true);
/// </code>
/// </example>
public interface IPhysxApiProvider3D
{
    /// <summary>Force/impulse commands.</summary>
    IPhysxForceApi3D Force { get; }
    /// <summary>Velocity read/write.</summary>
    IPhysxMotionApi3D Motion { get; }
    /// <summary>Pose read/write.</summary>
    IPhysxTransformApi3D Transform { get; }
}
