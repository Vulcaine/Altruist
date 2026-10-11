using System.Numerics;
using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Tests.Altruist.Physx.ThreeD;

/// <summary>A real BEPU engine plus the BEPU providers, for engine-level tests.</summary>
internal sealed class BepuTestWorld : IDisposable
{
    public BepuWorldEngine3D Engine { get; }
    public BepuPhysxBodyApiProvider3D Bodies { get; } = new();
    public BepuPhysxColliderApiProvider3D Colliders { get; } = new();

    public BepuTestWorld(Vector3? gravity = null)
    {
        Engine = new BepuWorldEngine3D(gravity ?? Vector3.Zero, 1f / 60f);
    }

    public IPhysxBody3D Static(Vector3 position, Vector3 halfExtents, PhysxTag? tag = null)
    {
        var body = Bodies.CreateBody(Engine, PhysxBody3D.Create(
            PhysxBodyType.Static, 0f, Transform3D.From(position, Quaternion.Identity, halfExtents), physxTag: tag));
        Engine.AddBody(body);
        return body;
    }

    public IPhysxBody3D Dynamic(Vector3 position, float mass = 1f, Vector3? halfExtents = null)
    {
        var body = Bodies.CreateBody(Engine, PhysxBody3D.Create(
            PhysxBodyType.Dynamic, mass, Transform3D.From(position, Quaternion.Identity, halfExtents ?? new Vector3(0.5f))));
        Engine.AddBody(body);
        return body;
    }

    public IPhysxBody3D Kinematic(Vector3 position)
    {
        var body = Bodies.CreateBody(Engine, PhysxBody3D.Create(
            PhysxBodyType.Kinematic, 0f, Transform3D.From(position, Quaternion.Identity, new Vector3(0.5f)), isKinematic: true));
        Engine.AddBody(body);
        return body;
    }

    public IPhysxCollider3D Attach(IPhysxBody3D body, in PhysxCollider3DDesc desc)
    {
        var collider = Colliders.CreateCollider(desc);
        Bodies.AddCollider(Engine, body, collider);
        return collider;
    }

    public void StepSeconds(float seconds)
    {
        int steps = (int)MathF.Round(seconds / Engine.FixedDeltaTime);
        for (int i = 0; i < steps; i++)
            Engine.Step(Engine.FixedDeltaTime);
    }

    public void Dispose() => Engine.Dispose();
}
