/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.TwoD.Numerics;

namespace Altruist.Gaming.TwoD
{
    public interface IPrefab2D : IPrefab, IVaultModel
    {
        Transform2D Transform { get; set; }

        /// <summary>Flat list of children: StorageId + Keyspace + Type.</summary>
        List<PrefabChildRef> Edges { get; set; }
    }

    public class Prefab2D : VaultModel, IPrefab2D
    {
        public override string Type { get; set; } = "prefab.manifest.2d";
        public override DateTime Timestamp { get; set; }

        public override string StorageId { get; set; } = "";
        public virtual string InstanceId { get; set; } = "";
        public virtual string RoomId { get; set; } = "";

        public virtual string PrefabId { get; set; } = "";
        public virtual string? DisplayName { get; set; }

        public virtual Transform2D Transform { get; set; } = Transform2D.Zero;

        /// <summary>Flat list of children (no hierarchy).</summary>
        public List<PrefabChildRef> Edges { get; set; } = new();
    }
}
