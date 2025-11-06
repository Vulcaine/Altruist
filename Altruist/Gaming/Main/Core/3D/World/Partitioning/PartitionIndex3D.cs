namespace Altruist.Gaming.ThreeD
{
    public class PartitionIndex3D
    {
        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        public PartitionIndex3D(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public override bool Equals(object? obj) =>
            obj is PartitionIndex3D other && X == other.X && Y == other.Y && Z == other.Z;

        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    }
}