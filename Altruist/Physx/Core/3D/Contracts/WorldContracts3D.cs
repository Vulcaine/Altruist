using System.Numerics;

using Altruist.Physx.Contracts;

namespace Altruist.Physx.ThreeD
{
    public interface IPhysxWorldEngine3D : IDisposable
    {
        float FixedDeltaTime { get; }
        IReadOnlyCollection<IPhysxBody3D> Bodies { get; }

        void Step(float deltaTime);

        void RemoveBody(IPhysxBody3D body);

        void AddBody(IPhysxBody3D body);

        IEnumerable<PhysxRaycastHit3D> RayCast(PhysxRay3D ray, int maxHits = 1, uint layerMask = 0xFFFFFFFFu);

        IEnumerable<PhysxRaycastHit3D> CapsuleCast(
            Vector3 center,
            float radius,
            float halfLength,
            Vector3 direction,
            float maxDistance,
            int maxHits = 1,
            uint layerMask = 0xFFFFFFFFu);
    }

    public interface IPhysxWorldEngineFactory3D
    {
        IPhysxWorldEngine3D GetExistingOrCreate(Vector3 gravity, float fixedDeltaTime = 1f / 60f);
    }

    public interface IPhysxWorld3D : IPhysxWorld
    {
        IPhysxWorldEngine3D Engine { get; }
        void AddBody(IPhysxBody3D body);
        IEnumerable<PhysxRaycastHit3D> RayCast(PhysxRay3D ray, int maxHits = 1);
    }
}

public sealed class HeightfieldData
{
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>World-space distance between samples along X.</summary>
    public float CellSizeX { get; init; }

    /// <summary>World-space distance between samples along Z.</summary>
    public float CellSizeZ { get; init; }

    /// <summary>Multiplier from stored height to world-space Y.</summary>
    public float HeightScale { get; init; }

    /// <summary>Heights[x, z] in whatever units you decided (usually 0..1 before HeightScale).</summary>
    public float[,] Heights { get; init; } = default!;
}
