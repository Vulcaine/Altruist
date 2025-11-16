using System.Numerics;

namespace Altruist.Gaming
{

    public interface IWorldIndex3D : IWorldIndex
    {
        Vector3 Position { get; set; }
        Vector3 Size { get; set; }
        Vector3 Gravity { get; set; }
    }

    [Service(typeof(IWorldIndex3D))]
    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "3D")]
    [ConditionalOnConfig("altruist:worlds:items", KeyField = "id")]
    public sealed class WorldIndex3D : VaultModel, IWorldIndex3D
    {
        public override string StorageId { get; set; }
        public override string GroupId { get; set; }
        public Vector3 Position { get; set; }
        public Vector3 Size { get; set; }
        public Vector3 Gravity { get; set; }
        public float FixedDeltaTime { get; set; }
        public int Index { get; set; }
        public override DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public override string Type { get; set; } = "WorldIndex3D";


        public WorldIndex3D(
            [AppConfigValue("*:index")]
            int index,
            [AppConfigValue("*:fixedDeltaTime", "0.01666f")]
            float fixedDeltaTime,
            [AppConfigValue("*:size")]
            Vector3 size,
            [AppConfigValue("*:gravity")]
            Vector3? gravity = null,
            [AppConfigValue("*:position")]
            Vector3? position = null,
            string? groupId = null)
        {
            StorageId = Guid.NewGuid().ToString();
            GroupId = groupId ?? string.Empty;
            Index = index;
            Size = size;
            FixedDeltaTime = fixedDeltaTime;
            Gravity = gravity ?? new Vector3(0f, 9.81f, 0f);
            Position = position ?? Vector3.Zero;
        }

        public int Width => (int)Size.X;
        public int Height => (int)Size.Y;
        public int Depth => (int)Size.Z;
    }
}
