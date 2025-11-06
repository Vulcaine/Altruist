namespace Altruist.Gaming.TwoD
{
    public class PartitionIndex2D
    {
        public int X { get; }
        public int Y { get; }

        public PartitionIndex2D(int x, int y)
        {
            X = x;
            Y = y;
        }

        public override bool Equals(object? obj) =>
            obj is PartitionIndex2D other && X == other.X && Y == other.Y;

        public override int GetHashCode() => HashCode.Combine(X, Y);
    }
}