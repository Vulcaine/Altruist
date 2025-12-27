/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming.ThreeD;
using Altruist.UORM;

namespace Altruist.Gaming
{

    public interface ISteppable3D
    {
        Task Step(float dt, IWorldPhysics3D worldPhysics);
    }

    /// <summary>
    /// Dimension-agnostic world object contract.
    /// Shared by 2D and 3D world objects.
    /// </summary>
    public interface IWorldObject : ISteppable3D
    {
        /// <summary>
        ///  Indicates whether the object has expired and should be removed from the world.
        /// </summary>
        [VaultIgnore]
        bool Expired { get; set; }

        /// <summary>Logical unique instance id in the world.</summary>
        string InstanceId { get; set; }

        /// <summary>High-level gameplay category (e.g. "Tree", "Rock", "House").</summary>
        string? ObjectArchetype { get; set; }

        /// <summary>Optional room/zone id for sharding.</summary>
        string ZoneId { get; set; }

    }
}
