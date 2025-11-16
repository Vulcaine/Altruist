using System.Numerics;

namespace Altruist.Gaming
{

    public interface IWorldIndex2D : IWorldIndex
    {
        Vector2 Position { get; set; }
        Vector2 Size { get; set; }
        Vector2 Gravity { get; set; }
    }

    [Service(typeof(IWorldIndex2D))]
    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "2D")]
    [ConditionalOnConfig("altruist:worlds:items", KeyField = "id")]
    public sealed class WorldIndex2D : VaultModel, IWorldIndex2D
    {
        public override string StorageId { get; set; }
        public override string GroupId { get; set; }
        public Vector2 Position { get; set; }
        public Vector2 Size { get; set; }
        public Vector2 Gravity { get; set; }
        public float FixedDeltaTime { get; set; }
        public int Index { get; set; }
        public override DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public override string Type { get; set; } = "WorldIndex2D";

        public WorldIndex2D(
            [AppConfigValue("altruist:game:worlds:items:*:index")]
            int index,
            [AppConfigValue("altruist:game:worlds:items:*:fixedDeltaTime", "0.01666f")]
            float fixedDeltaTime,
            [AppConfigValue("altruist:game:worlds:items:*:size")]
            Vector2 size,
            [AppConfigValue("altruist:game:worlds:items:*:gravity")]
            Vector2? gravity = null,
            [AppConfigValue("altruist:game:worlds:items:*:position")]
            Vector2? position = null,
            string? groupId = null)
        {
            StorageId = Guid.NewGuid().ToString();
            GroupId = groupId ?? string.Empty;
            Index = index;
            Size = size;
            FixedDeltaTime = fixedDeltaTime;
            Gravity = gravity ?? new Vector2(0f, 9.81f);
            Position = position ?? Vector2.Zero;
        }

        public int Width => (int)Size.X;
        public int Height => (int)Size.Y;
    }
}
