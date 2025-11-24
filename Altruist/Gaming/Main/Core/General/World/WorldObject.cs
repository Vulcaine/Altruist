/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming
{
    /// <summary>
    /// Dimension-agnostic world object contract.
    /// Shared by 2D and 3D world objects.
    /// </summary>
    public interface IWorldObject
    {
        /// <summary>Logical unique instance id in the world.</summary>
        string InstanceId { get; set; }

        /// <summary>High-level gameplay category (e.g. "Tree", "Rock", "House").</summary>
        string Archetype { get; set; }

        /// <summary>Optional room/zone id for sharding.</summary>
        string ZoneId { get; set; }
    }
}
