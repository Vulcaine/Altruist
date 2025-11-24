using System.Numerics;

using Altruist.Numerics;

namespace Altruist.Gaming
{
    public interface IWorldIndex2D : IWorldIndex
    {
        Vector2 Position { get; set; }
        IntVector2 Size { get; set; }
        Vector2 Gravity { get; set; }
    }

    [Service(typeof(IWorldIndex2D))]
    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "2D")]
    [ConditionalOnConfig("altruist:game:worlds:items", KeyField = "id")]
    public sealed class WorldIndex2D : VaultModel, IWorldIndex2D
    {
        public override string StorageId { get; set; }
        public override string GroupId { get; set; }

        public string? DataPath { get; set; }
        public Vector2 Position { get; set; }
        public IntVector2 Size { get; set; }
        public Vector2 Gravity { get; set; }
        public float FixedDeltaTime { get; set; }
        public int Index { get; set; }
        public override DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public override string Type { get; set; } = "WorldIndex2D";

        public WorldIndex2D(
            [AppConfigValue("*:index")]
            int index,
            [AppConfigValue("*:fixedDeltaTime", "0.01666f")]
            float fixedDeltaTime,
            [AppConfigValue("*:size")]
            IntVector2 size,
            [AppConfigValue("*:gravity")]
            Vector2? gravity = null,
            [AppConfigValue("*:position")]
            Vector2? position = null,
            [AppConfigValue("*:data")]
            string? data = null,
            string? groupId = null)
        {
            StorageId = Guid.NewGuid().ToString();
            GroupId = groupId ?? string.Empty;
            Index = index;
            Size = size;
            FixedDeltaTime = fixedDeltaTime;
            Gravity = gravity ?? new Vector2(0f, 9.81f);
            Position = position ?? Vector2.Zero;
            DataPath = data;
        }

        public int Width => Size.X;
        public int Height => Size.Y;
    }
}
