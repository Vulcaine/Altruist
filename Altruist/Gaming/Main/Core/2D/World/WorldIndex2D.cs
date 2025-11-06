using System.Numerics;

namespace Altruist.Gaming
{
    public sealed class WorldIndex2D : VaultModel
    {
        public override string SysId { get; set; }
        public override string GroupId { get; set; }
        public Vector2 Position { get; set; }
        public Vector2 Size { get; set; }
        public Vector2 Gravity { get; set; }
        public int Index { get; set; }
        public override DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public override string Type { get; set; } = "WorldIndex2D";

        public WorldIndex2D(
            int index,
            Vector2 size,
            Vector2? gravity = null,
            Vector2? position = null,
            string? groupId = null)
        {
            SysId = Guid.NewGuid().ToString();
            GroupId = groupId ?? string.Empty;
            Index = index;
            Size = size;
            Gravity = gravity ?? new Vector2(0f, 9.81f);
            Position = position ?? Vector2.Zero;
        }

        public int Width => (int)Size.X;
        public int Height => (int)Size.Y;
    }
}