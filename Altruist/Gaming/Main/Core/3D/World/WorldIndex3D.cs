using System.Numerics;

using Altruist.Numerics;

namespace Altruist.Gaming
{

    public interface IWorldIndex3D : IWorldIndex
    {
        Vector3 Position { get; set; }
        IntVector3 Size { get; set; }
        Vector3 Gravity { get; set; }

    }

    [Service(typeof(IWorldIndex3D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    [ConditionalOnConfig("altruist:game:worlds:items", KeyField = "id")]
    public sealed class WorldIndex3D : VaultModel, IWorldIndex3D
    {
        public override string StorageId { get; set; }
        public string? DataPath { get; set; }
        public Vector3 Position { get; set; }
        public IntVector3 Size { get; set; }
        public Vector3 Gravity { get; set; }
        public float FixedDeltaTime { get; set; }
        public int Index { get; set; }
        public string Name { get; set; }
        public override DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public override string Type { get; set; } = "WorldIndex3D";

        public WorldIndex3D(
            [AppConfigValue("*:index")]
            int index,
            [AppConfigValueAttribute("*:name")]
            string name,
            [AppConfigValue("*:fixedDeltaTime", "0.01666f")]
            float fixedDeltaTime,
            [AppConfigValue("*:size")]
            IntVector3 size,
            [AppConfigValue("*:gravity")]
            Vector3? gravity = null,
            [AppConfigValue("*:position")]
             Vector3? position = null,
            [AppConfigValue("*:data-path")]
            string? data = null)
        {
            StorageId = Guid.NewGuid().ToString();
            Index = index;
            Size = size;
            FixedDeltaTime = fixedDeltaTime;
            Gravity = gravity ?? new Vector3(0f, 9.81f, 0f);
            Position = position ?? Vector3.Zero;
            DataPath = data;
            Name = name;
        }

        public int Width => Size.X;
        public int Height => Size.Y;
        public int Depth => Size.Z;
    }
}
