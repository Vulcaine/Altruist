using System.Numerics;

namespace Altruist.Gaming
{
    public sealed class WorldIndex3D : VaultModel
    {
        public override string SysId { get; set; }
        public override string GroupId { get; set; }
        public Vector3 Position { get; set; }
        public Vector3 Size { get; set; }
        public Vector3 Gravity { get; set; }
        public int Index { get; set; }
        public override DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public override string Type { get; set; } = "WorldIndex3D";

        public WorldIndex3D(
            int index,
            Vector3 size,
            Vector3? gravity = null,
            Vector3? position = null,
            string? groupId = null)
        {
            SysId = Guid.NewGuid().ToString();
            GroupId = groupId ?? string.Empty;
            Index = index;
            Size = size;
            Gravity = gravity ?? new Vector3(0f, 9.81f, 0f);
            Position = position ?? Vector3.Zero;
        }

        public int Width => (int)Size.X;
        public int Height => (int)Size.Y;
        public int Depth => (int)Size.Z;
    }
}