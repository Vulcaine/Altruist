/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.ThreeD.Numerics;

namespace Altruist.Gaming.ThreeD
{
    public interface IPrefab3D : IPrefab, IVaultModel
    {
        Transform3D Transform { get; set; }

        /// <summary>Flat list of children: StorageId + Keyspace + Type.</summary>
        List<PrefabChildRef> Edges { get; set; }
    }

    public class Prefab3D : VaultModel, IPrefab3D
    {
        public override string Type { get; set; } = "prefab.manifest.3d";
        public override DateTime Timestamp { get; set; }

        public override string StorageId { get; set; } = "";
        public virtual string InstanceId { get; set; } = "";
        public virtual string RoomId { get; set; } = "";

        public virtual string PrefabId { get; set; } = "";
        public virtual string? DisplayName { get; set; }

        public virtual Transform3D Transform { get; set; } = Transform3D.Zero;

        /// <summary>Flat list of children (no hierarchy).</summary>
        public List<PrefabChildRef> Edges { get; set; } = new();
    }
}
